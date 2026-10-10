function Set-OrdinaryUserNamespaceWorkspacePermissions {
    param([Parameter(Mandatory)][string]$Workspace, [Parameter(Mandatory)][string]$WorkDirectory,
        [Parameter(Mandatory)][Security.Principal.SecurityIdentifier]$UserSid)

    $workspacePath = [IO.Path]::GetFullPath($Workspace)
    $workPath = [IO.Path]::GetFullPath($WorkDirectory)
    $marker = Join-Path $workspacePath '.mp-namespace-ci'
    if ([IO.Path]::GetDirectoryName($workPath) -cne $workspacePath -or
        [IO.Path]::GetFileName($workspacePath) -cnotmatch '^[0-9a-f]{32}$' -or
        -not (Test-Path -LiteralPath $marker -PathType Leaf) -or
        [IO.File]::ReadAllText($marker) -cne [IO.Path]::GetFileName($workspacePath)) {
        throw 'The ordinary-user workspace permissions require the marked disposable directory.'
    }
    $inheritance = [Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit'
    foreach ($target in @(@{path=$workspacePath;rights='ReadAndExecute'}, @{path=$workPath;rights='FullControl'})) {
        $directory = [IO.DirectoryInfo]::new($target.path)
        if ($directory.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'A disposable permission target must not be a reparse point.' }
        # These are .NET extension methods; PowerShell does not expose them as instance methods.
        $acl = [IO.FileSystemAclExtensions]::GetAccessControl($directory, [Security.AccessControl.AccessControlSections]::Access)
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($UserSid, $target.rights, $inheritance, 'None', 'Allow'))
        [IO.FileSystemAclExtensions]::SetAccessControl($directory, $acl)
    }
}
