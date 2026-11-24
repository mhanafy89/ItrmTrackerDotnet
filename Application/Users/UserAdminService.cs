using ItrmTracker.Application.Abstractions;
using ItrmTracker.Application.Common;
using ItrmTracker.Application.Tracker;
using ItrmTracker.Domain.Constants;
using ItrmTracker.Domain.Entities;

namespace ItrmTracker.Application.Users;

/// <summary>User row as listed in the administration screen.</summary>
public sealed record UserListItem(int Id, string Username, string Name, string Role, bool IsActive, DateTime CreatedAt);

/// <summary>Persistence of dbo.users.</summary>
public interface IUserRepository
{
    Task<List<UserListItem>> ListAsync(CancellationToken ct = default);
    Task<AppUser?> FindAsync(int id, CancellationToken ct = default);
    Task<int> CreateAsync(AppUser user, CancellationToken ct = default);
    Task UpdateAsync(AppUser user, CancellationToken ct = default);
    Task<bool> DeleteAsync(int id, CancellationToken ct = default);
    Task<int> CountAsync(CancellationToken ct = default);
}

/// <summary>Password hashing abstraction (implemented with ASP.NET Core Identity + legacy scrypt support).</summary>
public interface IPasswordService
{
    string Hash(AppUser user, string password);
    bool Verify(AppUser user, string password);
}

public sealed record CreateUserInput(string? Username, string? Password, string? Name, string? Role);

public sealed record UpdateUserInput(string? Name, string? Role, bool? IsActive, string? Password);

public interface IUserAdminService
{
    Task<List<UserListItem>> ListAsync(CancellationToken ct = default);
    Task<AppUser> GetAsync(int id, CancellationToken ct = default);
    Task<UserListItem> CreateAsync(CreateUserInput input, CancellationToken ct = default);
    Task<UserListItem> UpdateAsync(int id, UpdateUserInput input, CancellationToken ct = default);
    Task DeleteAsync(int id, CancellationToken ct = default);

    /// <summary>Any logged-in user (read-only included) can change their own password.</summary>
    Task ChangeOwnPasswordAsync(string? currentPassword, string? newPassword, CancellationToken ct = default);
}

/// <summary>User administration and self-service password change (port of routes/auth.js).</summary>
public sealed class UserAdminService : IUserAdminService
{
    private const int MinPasswordLength = 6;
    private readonly IUserRepository _repo;
    private readonly IPasswordService _passwords;
    private readonly ICurrentUser _current;
    private readonly IClock _clock;
    private readonly IAuditLogger _audit;

    public UserAdminService(IUserRepository repo, IPasswordService passwords, ICurrentUser current, IClock clock, IAuditLogger audit)
    {
        _repo = repo;
        _passwords = passwords;
        _current = current;
        _clock = clock;
        _audit = audit;
    }

    public Task<List<UserListItem>> ListAsync(CancellationToken ct = default) => _repo.ListAsync(ct);

    public async Task<AppUser> GetAsync(int id, CancellationToken ct = default) =>
        (id > 0 ? await _repo.FindAsync(id, ct) : null) ?? throw ServiceException.NotFound("User not found.");

    public async Task<UserListItem> CreateAsync(CreateUserInput input, CancellationToken ct = default)
    {
        var username = (input.Username ?? string.Empty).Trim();
        var password = input.Password ?? string.Empty;
        var name = (input.Name ?? string.Empty).Trim();
        var role = input.Role ?? string.Empty;
        var errors = new List<string>();
        if (username.Length == 0) errors.Add("Username is required.");
        if (name.Length == 0) errors.Add("Name is required.");
        if (password.Length < MinPasswordLength) errors.Add("Password must be at least 6 characters.");
        if (!AppRoles.IsValid(role)) errors.Add("Role must be 'read_only' or 'read_write'.");
        if (errors.Count > 0) throw new ServiceException(400, errors);

        var now = _clock.UtcNow;
        var user = new AppUser { Username = username, Name = name, Role = role, IsActive = true, CreatedAt = now, UpdatedAt = now };
        user.PasswordHash = _passwords.Hash(user, password);
        try
        {
            user.Id = await _repo.CreateAsync(user, ct);
        }
        catch (DuplicateKeyException)
        {
            throw ServiceException.Conflict("That username is already taken.");
        }
        _audit.Log("user.create", "users", user.Id, $"{username} ({role})");
        return new UserListItem(user.Id, username, name, role, true, now);
    }

    public async Task<UserListItem> UpdateAsync(int id, UpdateUserInput input, CancellationToken ct = default)
    {
        if (id <= 0) throw ServiceException.BadRequest("Invalid user id.");
        var existing = await _repo.FindAsync(id, ct) ?? throw ServiceException.NotFound("User not found.");

        var name = input.Name is not null ? input.Name.Trim() : existing.Name;
        var role = input.Role ?? existing.Role;
        var isActive = input.IsActive ?? existing.IsActive;
        if (name.Length == 0) throw ServiceException.BadRequest("Name is required.");
        if (!AppRoles.IsValid(role)) throw ServiceException.BadRequest("Role must be 'read_only' or 'read_write'.");
        if (!isActive && id == _current.UserId) throw ServiceException.BadRequest("You cannot deactivate your own account.");

        if (!string.IsNullOrEmpty(input.Password))
        {
            if (input.Password.Length < MinPasswordLength) throw ServiceException.BadRequest("Password must be at least 6 characters.");
            existing.PasswordHash = _passwords.Hash(existing, input.Password);
        }
        existing.Name = name;
        existing.Role = role;
        existing.IsActive = isActive;
        existing.UpdatedAt = _clock.UtcNow;
        await _repo.UpdateAsync(existing, ct);
        _audit.Log("user.update", "users", id, $"role={role} active={isActive} passwordReset={!string.IsNullOrEmpty(input.Password)}");
        return new UserListItem(id, existing.Username, name, role, isActive, existing.CreatedAt);
    }

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        if (id <= 0) throw ServiceException.BadRequest("Invalid user id.");
        if (id == _current.UserId) throw ServiceException.BadRequest("You cannot delete your own account.");
        if (!await _repo.DeleteAsync(id, ct)) throw ServiceException.NotFound("User not found.");
        _audit.Log("user.delete", "users", id);
    }

    public async Task ChangeOwnPasswordAsync(string? currentPassword, string? newPassword, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(currentPassword) || string.IsNullOrEmpty(newPassword)) throw ServiceException.BadRequest("Current and new password are required.");
        if (newPassword.Length < MinPasswordLength) throw ServiceException.BadRequest("New password must be at least 6 characters.");
        var user = _current.UserId is { } uid ? await _repo.FindAsync(uid, ct) : null;
        if (user is null || !_passwords.Verify(user, currentPassword)) throw new ServiceException(401, "Current password is incorrect.");
        user.PasswordHash = _passwords.Hash(user, newPassword);
        user.UpdatedAt = _clock.UtcNow;
        await _repo.UpdateAsync(user, ct);
        _audit.Log("user.change-password", "users", user.Id);
    }
}
