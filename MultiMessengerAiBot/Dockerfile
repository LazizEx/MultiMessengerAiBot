# Build stage
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Копируем csproj из подпапки
COPY MultiMessengerAiBot/*.csproj ./MultiMessengerAiBot/
WORKDIR /src/MultiMessengerAiBot
RUN dotnet restore

# Копируем остальной код
WORKDIR /src
COPY MultiMessengerAiBot/. ./MultiMessengerAiBot/

WORKDIR /src/MultiMessengerAiBot
RUN dotnet publish -c Release -o /app/publish --no-restore

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_URLS=http://0.0.0.0:$PORT
ENTRYPOINT ["dotnet", "MultiMessengerAiBot.dll"]  # ← имя dll от твоего проекта