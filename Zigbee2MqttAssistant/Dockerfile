FROM mcr.microsoft.com/dotnet/core/aspnet:2.2-stretch-slim AS base
WORKDIR /app
EXPOSE 80
EXPOSE 443

FROM mcr.microsoft.com/dotnet/core/sdk:2.2-stretch AS build
WORKDIR /src
COPY ["Zigbee2MqttAssistant/Zigbee2MqttAssistant.csproj", "Zigbee2MqttAssistant/"]
RUN dotnet restore "Zigbee2MqttAssistant/Zigbee2MqttAssistant.csproj"
COPY . .
WORKDIR "/src/Zigbee2MqttAssistant"
RUN dotnet build "Zigbee2MqttAssistant.csproj" -c Release -o /app

FROM build AS publish
RUN dotnet publish "Zigbee2MqttAssistant.csproj" -c Release -o /app

FROM base AS final
WORKDIR /app
COPY --from=publish /app .
ENTRYPOINT ["dotnet", "Zigbee2MqttAssistant.dll"]