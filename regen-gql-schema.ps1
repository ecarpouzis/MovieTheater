$ErrorActionPreference = "Stop"

try {
	dotnet run --project "$PSScriptRoot/src/MovieTheater/MovieTheater.csproj" export gql "$PSScriptRoot/src/ui/src/schema.graphql"

	if ($LASTEXITCODE -ne 0) {
		Write-Host "Failed to export schema. See the log for details."
		Read-Host "Press enter to exit..."
		Exit
	}

	$schema = [System.IO.File]::ReadAllText("$PSScriptRoot/src/ui/src/schema.graphql")

	$schema = $schema.Replace("``", "\``")

	$schemaJsTemplate = @"

const schema = ``{0}``

export default schema
"@

	$schemaJs = [System.String]::Format($schemaJsTemplate, $schema)

	[System.IO.File]::WriteAllText("$PSScriptRoot/src/ui/src/schema.js", $schemaJs)
}
catch {
	Read-Host "Press enter to exit..."
}