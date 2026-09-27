# Beta Censoring server with NudeNet v3 support, built from the patched sources in this folder.
#   docker build -t beta-censoring-v3 .
#   docker run -d -p 2382:2382 --name beta-censoring beta-censoring-v3
# Choose the model with BCS_ModelPath (/app/models/640m.onnx or /app/models/320n.onnx).

# Status page front end (the embedded web UI at http://<host>:2382)
FROM node:16 AS status-ui
WORKDIR /src/ClientApp
COPY beta-censoring/src/BetaCensor.Web.Status/ClientApp/package.json beta-censoring/src/BetaCensor.Web.Status/ClientApp/package-lock.json ./
RUN npm ci --no-audit --no-fund
COPY beta-censoring/src/BetaCensor.Web.Status/ClientApp/ ./
# vite outputs to ../wwwroot/dist
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:6.0 AS build
WORKDIR /src
COPY censor-core/ censor-core/
COPY beta-censoring/ beta-censoring/
COPY --from=status-ui /src/wwwroot/dist beta-censoring/src/BetaCensor.Web.Status/wwwroot/dist
RUN dotnet publish beta-censoring/src/BetaCensor.Server/BetaCensor.Server.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:6.0
WORKDIR /app
COPY --from=build /app .
COPY models/320n.onnx models/640m.onnx ./models/
ENV BCS_ModelPath=/app/models/640m.onnx
EXPOSE 2382
ENTRYPOINT ["dotnet", "BetaCensor.Server.dll"]
