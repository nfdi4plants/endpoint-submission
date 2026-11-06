FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy only the project and source files. A local build leaves bin/ and obj/ in this folder,
# and they must not reach the image build.
COPY MockBadge.csproj ./
RUN dotnet restore MockBadge.csproj
COPY Program.cs ./
RUN dotnet publish MockBadge.csproj -c Release -o /app/publish --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
EXPOSE 8080
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "MockBadge.dll"]
