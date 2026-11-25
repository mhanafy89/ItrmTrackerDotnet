using FluentValidation;
using ItrmTracker.Application.Sla;
using ItrmTracker.Application.Validation;
using ItrmTracker.Domain.Constants;
using ItrmTracker.Web.ViewModels;

namespace ItrmTracker.Web.Validators;

public sealed class LoginViewModelValidator : AbstractValidator<LoginViewModel>
{
    public LoginViewModelValidator()
    {
        RuleFor(x => x.Username).NotEmpty().WithMessage("Username and password are required.").MaximumLength(100);
        RuleFor(x => x.Password).NotEmpty().WithMessage("Username and password are required.");
    }
}

public sealed class ChangePasswordViewModelValidator : AbstractValidator<ChangePasswordViewModel>
{
    public ChangePasswordViewModelValidator()
    {
        RuleFor(x => x.CurrentPassword).NotEmpty().WithMessage("Current and new password are required.");
        RuleFor(x => x.NewPassword).NotEmpty().WithMessage("Current and new password are required.")
            .MinimumLength(6).WithMessage("New password must be at least 6 characters.");
        RuleFor(x => x.ConfirmNewPassword).Equal(x => x.NewPassword).WithMessage("The new passwords do not match.");
    }
}

public sealed class UserCreateViewModelValidator : AbstractValidator<UserCreateViewModel>
{
    public UserCreateViewModelValidator()
    {
        RuleFor(x => x.Username).NotEmpty().WithMessage("Username is required.").MaximumLength(100);
        RuleFor(x => x.Name).NotEmpty().WithMessage("Name is required.").MaximumLength(200);
        RuleFor(x => x.Password).NotEmpty().MinimumLength(6).WithMessage("Password must be at least 6 characters.");
        RuleFor(x => x.Role).Must(AppRoles.IsValid).WithMessage("Role must be 'read_only' or 'read_write'.");
    }
}

public sealed class UserEditViewModelValidator : AbstractValidator<UserEditViewModel>
{
    public UserEditViewModelValidator()
    {
        RuleFor(x => x.Name).NotEmpty().WithMessage("Name is required.").MaximumLength(200);
        RuleFor(x => x.Role).Must(AppRoles.IsValid).WithMessage("Role must be 'read_only' or 'read_write'.");
        RuleFor(x => x.Password).MinimumLength(6).When(x => !string.IsNullOrEmpty(x.Password)).WithMessage("Password must be at least 6 characters.");
    }
}

public sealed class CommentInputModelValidator : AbstractValidator<CommentInputModel>
{
    public CommentInputModelValidator()
    {
        RuleFor(x => x.Body).NotEmpty().WithMessage("Comment body is required.")
            .MaximumLength(TrackerRules.CommentMaxLength).WithMessage($"Comments are limited to {TrackerRules.CommentMaxLength} characters.");
    }
}

public sealed class LookupNameInputModelValidator : AbstractValidator<LookupNameInputModel>
{
    public LookupNameInputModelValidator()
    {
        RuleFor(x => x.Name).NotEmpty().WithMessage("name is required.").MaximumLength(200);
    }
}

public sealed class EmailDraftInputModelValidator : AbstractValidator<EmailDraftInputModel>
{
    public EmailDraftInputModelValidator()
    {
        RuleFor(x => x.Ids).NotEmpty().WithMessage("No selected records are visible.");
        RuleFor(x => x.Subject).MaximumLength(255);
        RuleFor(x => x.To).MaximumLength(2000);
        RuleFor(x => x.Cc).MaximumLength(2000);
        RuleFor(x => x.Message).MaximumLength(10000);
    }
}

/// <summary>Same limits as the stage action dialogs (the engine re-validates with the original messages).</summary>
public sealed class StageActionInputModelValidator : AbstractValidator<StageActionInputModel>
{
    public StageActionInputModelValidator()
    {
        RuleFor(x => x.Stage).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Status).NotEmpty().Must(s => SlaEngine.CanonicalStatus(s) is not null).WithMessage($"status must be one of: {string.Join(", ", StageStatus.All)}.");
        RuleFor(x => x.Note).MaximumLength(500).WithMessage("note must be at most 500 characters.");
    }
}

/// <summary>
/// Request form: runs the ported server-side rules (title, date formats/ranges and the two-sided pipeline date order)
/// so errors are shown next to the form with the original wording.
/// </summary>
public sealed class TrackerFormViewModelValidator : AbstractValidator<TrackerFormViewModel>
{
    public TrackerFormViewModelValidator()
    {
        RuleFor(x => x).Custom((model, context) =>
        {
            var body = model.ToInput().ToValidationBody();
            var errors = model.Kind == Application.Sla.TrackerKind.Incident
                ? RequestValidation.ValidateIncident(body)
                : RequestValidation.ValidateBusinessRequest(body);
            foreach (var error in errors) context.AddFailure(string.Empty, error);
        });
    }
}
