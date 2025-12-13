# Stage 1: Build the application using the SDK image
FROM mcr.microsoft.com AS build
WORKDIR /src
COPY ["MultiMessengerAiBot.csproj", "./"]
RUN dotnet restore "MultiMessengerAiBot.csproj"

COPY . .
WORKDIR "/src/"
RUN dotnet publish "MultiMessengerAiBot.csproj" -c Release -o /app/publish

# Stage 2: Create the final runtime image from the slim ASP.NET runtime
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
EXPOSE 80

# Copy the published output from the build stage to the final image
COPY --from=build /app/publish .

# Set the entry point for the container
ENTRYPOINT ["dotnet", "MultiMessengerAiBot.dll"]