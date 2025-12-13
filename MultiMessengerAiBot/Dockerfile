# Официальный образ для runtime
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
WORKDIR /app
EXPOSE 8080

# Сборка
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY . .
RUN dotnet restore
RUN dotnet publish -c Release -o /app/publish --no-restore

# Финальный образ
FROM base AS final
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_URLS=http://0.0.0.0:$PORT   # Render использует переменную $PORT
ENTRYPOINT ["dotnet", "MultiMessengerAiBot.dll"]  # ← замени на имя своего .dll (обычно имя проекта + .dll)
