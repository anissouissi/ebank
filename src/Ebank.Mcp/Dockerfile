FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source

COPY src/Ebank.Mcp/Ebank.Mcp.csproj src/Ebank.Mcp/
RUN dotnet restore src/Ebank.Mcp/Ebank.Mcp.csproj

COPY src/Ebank.Mcp/ src/Ebank.Mcp/
WORKDIR /source/src/Ebank.Mcp
RUN dotnet publish Ebank.Mcp.csproj --configuration Release --output /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "Ebank.Mcp.dll"]
