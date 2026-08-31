FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY ["CryptoSense.csproj", "./"]
RUN dotnet restore "CryptoSense.csproj"
COPY . .
RUN dotnet publish "CryptoSense.csproj" -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_URLS=http://+:5083
EXPOSE 5083
ENTRYPOINT ["dotnet", "CryptoSense.dll"]