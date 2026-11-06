FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY StudyTracker.csproj ./
RUN dotnet restore
COPY . ./
RUN dotnet publish -c Release --no-restore -o /out /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
USER root
RUN mkdir -p /data /keys && chown app:app /data /keys
COPY --from=build /out ./
ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_ENVIRONMENT=Production \
    Tracker__DataDirectory=/data \
    Tracker__KeyDirectory=/keys
USER app
EXPOSE 8080
ENTRYPOINT ["dotnet", "StudyTracker.dll"]
