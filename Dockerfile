# build
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src


COPY *.sln ./
COPY BlazorOptions.Server/BlazorOptions.Server.csproj BlazorOptions.Server/
COPY BlazorOptions/BlazorOptions.csproj BlazorOptions/
COPY BlazorOptions.API/BlazorOptions.API.csproj BlazorOptions.API/
RUN dotnet restore

COPY . .
RUN dotnet publish ./BlazorOptions.Server -c Release -o /app/publish

# runtime
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "BlazorOptions.Server.dll"]
