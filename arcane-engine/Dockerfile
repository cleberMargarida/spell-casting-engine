FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY arcane-engine.csproj .
RUN dotnet restore
COPY . .
RUN dotnet publish -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
EXPOSE 5000
ENTRYPOINT ["dotnet", "arcane-engine.dll"]
