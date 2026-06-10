# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/MoneyTransfer.Api/MoneyTransfer.Api.csproj src/MoneyTransfer.Api/
RUN dotnet restore src/MoneyTransfer.Api/MoneyTransfer.Api.csproj
COPY . .
RUN dotnet publish src/MoneyTransfer.Api/MoneyTransfer.Api.csproj -c Release -o /app /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "MoneyTransfer.Api.dll"]
