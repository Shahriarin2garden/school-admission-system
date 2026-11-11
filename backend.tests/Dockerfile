FROM mcr.microsoft.com/dotnet/sdk:8.0
WORKDIR /src
COPY SchoolAdmission.Tests.csproj ./
RUN dotnet restore
COPY . ./
ENTRYPOINT ["dotnet", "test", "--no-restore", "--logger", "console;verbosity=normal"]
