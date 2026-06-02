# --- Build stage ---
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore (layer-cached on project files)
COPY DiffPdf.slnx ./
COPY src/DiffPdf.Core/*.csproj src/DiffPdf.Core/
COPY src/DiffPdf.Pdf/*.csproj src/DiffPdf.Pdf/
COPY src/DiffPdf.Persistence/*.csproj src/DiffPdf.Persistence/
COPY src/DiffPdf.Worker/*.csproj src/DiffPdf.Worker/
COPY src/DiffPdf.Api/*.csproj src/DiffPdf.Api/
RUN dotnet restore src/DiffPdf.Api/DiffPdf.Api.csproj

COPY . .
RUN dotnet publish src/DiffPdf.Api/DiffPdf.Api.csproj -c Release -o /app

# --- Runtime stage ---
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Ghostscript (default renderer) + fontconfig/freetype for SkiaSharp & PDFium.
RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        ghostscript \
        libfontconfig1 \
        libfreetype6 \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app ./

ENV ASPNETCORE_URLS=http://+:8080
ENV DIFFPDF_ARTIFACT_ROOT=/data/artifacts
EXPOSE 8080
VOLUME ["/data"]

ENTRYPOINT ["dotnet", "DiffPdf.Api.dll"]
