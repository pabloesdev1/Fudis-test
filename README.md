docker compose up -d

export ASPNETCORE_ENVIRONMENT=Development 
export PATH="$PATH:$HOME/.dotnet/tools"

dotnet build

dotnet tool install --global dotnet-ef --version 8.*
dotnet ef migrations add Inicial -p src/Fudis.Api
dotnet ef database update -p src/Fudis.Api

dotnet run --project src/Fudis.Api

dotnet test
