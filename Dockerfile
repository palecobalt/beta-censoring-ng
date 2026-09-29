# Beta Censoring server with NudeNet v3 support, built from the sources in this repository.
# Run scripts/fetch-models.sh first: the models are copied from models/.
#   docker build -t beta-censoring-ng .
#   docker run -d -p 127.0.0.1:2382:2382 --name beta-censoring beta-censoring-ng
# (-p 2382:2382 instead makes it reachable from other machines too; it has no authentication)
# Choose the model with BCS_ModelPath (/app/models/640m.onnx or /app/models/320n.onnx).

# Status page front end (the embedded web UI at http://<host>:2382)
FROM node:16 AS status-ui
WORKDIR /src/ClientApp
COPY beta-censoring/src/BetaCensor.Web.Status/ClientApp/package.json beta-censoring/src/BetaCensor.Web.Status/ClientApp/package-lock.json ./
RUN npm ci --no-audit --no-fund
COPY beta-censoring/src/BetaCensor.Web.Status/ClientApp/ ./
# vite outputs to ../wwwroot/dist
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY censor-core/ censor-core/
COPY beta-censoring/ beta-censoring/
COPY --from=status-ui /src/wwwroot/dist beta-censoring/src/BetaCensor.Web.Status/wwwroot/dist
RUN dotnet publish beta-censoring/src/BetaCensor.Server/BetaCensor.Server.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
COPY models/320n.onnx models/640m.onnx ./models/
# inside the container it listens on every interface; which host addresses reach it is decided by -p / ports:
ENV BCS_ModelPath=/app/models/640m.onnx \
    BCS_Server__ListenAddress=*
EXPOSE 2382
ENTRYPOINT ["dotnet", "BetaCensor.Server.dll"]
