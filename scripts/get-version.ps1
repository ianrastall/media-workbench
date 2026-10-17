# Prints the application version. Directory.Build.props is the single source for builds, installer, and releases.
$ErrorActionPreference = 'Stop'
$taskProps = Join-Path (Split-Path -Parent $PSScriptRoot) 'Directory.Build.props'
$taskVersion = (Select-Xml -LiteralPath $taskProps -XPath '/Project/PropertyGroup/Version').Node.InnerText
if ($taskVersion -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.]+)?$') { throw "Invalid version in Directory.Build.props: '$taskVersion'" }
$taskVersion
