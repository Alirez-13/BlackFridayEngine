FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY ["src/BlackFriday.Api/BlackFriday.Api.csproj", "src/BlackFriday.Api/"]
RUN dotnet restore "src/BlackFriday.Api/BlackFriday.Api.csproj"
COPY . .
WORKDIR "/src/src/BlackFriday.Api"
RUN dotnet publish "BlackFriday.Api.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
RUN mkdir -p /app/data
COPY --from=build /app/publish .
ENV ASPNETCORE_URLS=http://+:8080
ENV Storage__DataDirectory=/app/data
EXPOSE 8080
ENTRYPOINT ["dotnet", "BlackFriday.Api.dll"]