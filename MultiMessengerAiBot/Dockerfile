# Этап сборки
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Копируем только .csproj и восстанавливаем зависимости
COPY MultiMessengerAiBot/*.csproj ./MultiMessengerAiBot/
WORKDIR /src/MultiMessengerAiBot
RUN dotnet restore "MultiMessengerAiBot.csproj"

# Копируем весь остальной код проекта
COPY MultiMessengerAiBot/. ./

# Публикуем приложение
RUN dotnet publish "MultiMessengerAiBot.csproj" -c Release -o /app/publish --no-restore

# Финальный runtime-образ
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app

# Копируем опубликованные файлы
COPY --from=build /app/publish ./

# Render автоматически задаёт переменную $PORT
ENV ASPNETCORE_URLS=http://0.0.0.0:$PORT

# Точка входа
ENTRYPOINT ["dotnet", "MultiMessengerAiBot.dll"]