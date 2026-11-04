FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Kubico.slnx ./
COPY src/Kubico.Domain/Kubico.Domain.csproj src/Kubico.Domain/
COPY src/Kubico.Infrastructure/Kubico.Infrastructure.csproj src/Kubico.Infrastructure/
COPY src/Kubico.Api/Kubico.Api.csproj src/Kubico.Api/
RUN dotnet restore src/Kubico.Api/Kubico.Api.csproj -r linux-x64 -p:PublishReadyToRun=true

COPY src/ src/
# ReadyToRun: o código já vai compilado para a máquina, e o arranque e os primeiros pedidos ficam mais rápidos (F4-20).
RUN dotnet publish src/Kubico.Api/Kubico.Api.csproj -c Release -o /app --no-restore -r linux-x64 --self-contained false     -p:PublishReadyToRun=true

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "Kubico.Api.dll"]
