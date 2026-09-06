FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY ["CryptoSense.csproj", "./"]
RUN dotnet restore "CryptoSense.csproj"
COPY . .
RUN dotnet publish "CryptoSense.csproj" -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final
WORKDIR /app
RUN apt-get update && apt-get install -y sqlite3 && rm -rf /var/lib/apt/lists/*
COPY --from=build /app/publish .
ENV ASPNETCORE_URLS=http://+:5083
EXPOSE 5083
RUN mkdir -p /app/data
ENTRYPOINT ["dotnet", "CryptoSense.dll"]