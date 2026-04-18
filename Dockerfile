# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY WarButBetterBackend/WarButBetterBackend.csproj WarButBetterBackend/
RUN dotnet restore WarButBetterBackend/WarButBetterBackend.csproj

COPY WarButBetterBackend/ WarButBetterBackend/
WORKDIR /src/WarButBetterBackend
RUN dotnet publish WarButBetterBackend.csproj -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app


EXPOSE 8080
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "WarButBetterBackend.dll"]
