
# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy project files
COPY VRAssetManagement.Api/VRAssetManagement.Api.csproj VRAssetManagement.Api/
COPY VRAssetManagement.Application/VRAssetManagement.Application.csproj VRAssetManagement.Application/
COPY VRAssetManagement.Domain/VRAssetManagement.Domain.csproj VRAssetManagement.Domain/
COPY VRAssetManagement.Infrastructure/VRAssetManagement.Infrastructure.csproj VRAssetManagement.Infrastructure/

# Restore dependencies
RUN dotnet restore VRAssetManagement.Api/VRAssetManagement.Api.csproj

# Copy source code
COPY . .

# Publish API
WORKDIR /src/VRAssetManagement.Api
RUN dotnet publish VRAssetManagement.Api.csproj -c Release -o /app/publish /p:UseAppHost=false

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

COPY --from=build /app/publish .

ENV ASPNETCORE_ENVIRONMENT=Production

EXPOSE 10000

ENTRYPOINT ["sh", "-c", "dotnet VRAssetManagement.Api.dll --urls http://0.0.0.0:${PORT:-10000}"]
