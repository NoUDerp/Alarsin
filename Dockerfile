FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:5.0 AS build
ARG TARGETPLATFORM
ARG BUILDPLATFORM
RUN mkdir /Bot && cd /Bot
WORKDIR /Bot
ENV DOTNET_CLI_TELEMETRY_OPTOUT 1
COPY Program.cs /Bot/Program.cs
COPY Alarsin.csproj /Bot/Alarsin.csproj
RUN dotnet publish -p:PublishReadyToRun=false /p:Platform=any -c Release
#RUN dotnet publish -p:PublishReadyToRun=false -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishSingleFile=true -p:PublishTrimmed=true --self-contained true -c Release -r linux-musl-x64 /Bot/Alarsin.csproj && chmod +x /Bot/bin/Release/net5.0/linux-musl-x64/publish/Alarsin
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/runtime:5.0-alpine
COPY --from=build /Bot/bin/any/Release/net5.0 /bot
RUN apk --no-cache add ca-certificates krb5-dev icu-libs
ENTRYPOINT ["/usr/bin/dotnet", "/bot/Alarsin.dll", "$@"]
