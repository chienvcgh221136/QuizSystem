# Use the official .NET 9 SDK image to build the app
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /app

# Copy csproj and restore dependencies
COPY *.csproj ./
RUN dotnet restore

# Copy the remaining files and build the app
COPY . ./
RUN dotnet publish -c Release -o out

# Use the official ASP.NET Core runtime image to run the app
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app
COPY --from=build /app/out ./

# Expose port 80 or 8080 (Render uses PORT env variable)
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080

ENTRYPOINT ["dotnet", "QuizApi.dll"]
