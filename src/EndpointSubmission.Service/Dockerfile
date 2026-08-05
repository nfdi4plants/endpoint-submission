FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
WORKDIR /app
EXPOSE 8080

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

# Restore against the project files alone, so the layer survives source changes.
COPY ["src/EndpointSubmission.Service/EndpointSubmission.Service.csproj", "src/EndpointSubmission.Service/"]
COPY ["src/ENAClient/ENAClient.fsproj", "src/ENAClient/"]
RUN dotnet restore "src/EndpointSubmission.Service/EndpointSubmission.Service.csproj"

COPY src/ src/
WORKDIR "/src/src/EndpointSubmission.Service"
RUN dotnet build "EndpointSubmission.Service.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "EndpointSubmission.Service.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "EndpointSubmission.Service.dll"]
