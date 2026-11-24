# Build
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY . .
RUN dotnet restore Web/ItrmTracker.Web.csproj \
 && dotnet publish Web/ItrmTracker.Web.csproj -c Release -o /app --no-restore

# Runtime (non-root user provided by the aspnet image)
FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_RUNNING_IN_CONTAINER=true
USER app
EXPOSE 8080
ENTRYPOINT ["dotnet", "ItrmTracker.Web.dll"]
