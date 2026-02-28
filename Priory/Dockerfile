FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY src/Priory.Game/Priory.Game.csproj src/Priory.Game/
RUN dotnet restore src/Priory.Game/Priory.Game.csproj
COPY src/Priory.Game/ src/Priory.Game/
RUN dotnet publish src/Priory.Game/Priory.Game.csproj -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish/ ./
COPY data ./data
RUN mkdir -p /app/saves
ENV PRIORY_SAVE_SECRET=CHANGE_ME
ENV PRIORY_SERVER_MODE=true
ENV ASPNETCORE_URLS=http://0.0.0.0:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "Priory.Game.dll"]
