Imports System
Imports System.Collections.Generic
Imports System.Linq
Imports JSql.Storage
Imports Nuget

' a throw away seeder which publishes two demo package versions and a couple of
' api document rows through the very same data access layer of the server, so
' that the sitemap of the running server can be inspected with real data.
Module Program

    Sub Main(args As String())
        Dim database As String = args.FirstOrDefault()

        If String.IsNullOrEmpty(database) Then
            database = "G:\xDoc\dist\data\db"
        End If

        Dim store As New NugetStore(database, New StorageOptions With {
            .MergeIdleSeconds = 30,
            .MergeAfterOperations = 2000,
            .FsyncEachWrite = False
        })

        For Each version As String In {"1.0.0", "1.2.3"}
            Dim pkg As PackageRecord = newPackage("Demo.Widgets", version)

            If Not store.PackageExists(pkg.package_id, pkg.version) Then
                Call store.AddPackage(pkg)
            End If

            Call Console.WriteLine($"package: {pkg.package_id} {pkg.version} -> id={pkg.id}")
        Next

        Dim rows As New List(Of PackageApiDocRecord)

        For Each typeName As String In {"Demo.Widgets.Engine", "Demo.Widgets.WidgetKind", "Demo.Widgets.WidgetServer"}
            Call rows.Add(New PackageApiDocRecord With {
                .package_id = "Demo.Widgets",
                .version = "1.2.3",
                .namespace_name = "Demo.Widgets",
                .namespace_summary = "the demo namespace of the seeder",
                .type_fullname = typeName,
                .type_name = typeName.Split("."c).Last(),
                .summary = "a demo type of the seeder",
                .member_count = 3,
                .payload = "{}"
            })
        Next

        Call store.ReplacePackageApiDocs("Demo.Widgets", "1.2.3", rows)
        Call Console.WriteLine($"api documents seeded: {rows.Count}")
    End Sub

    Private Function newPackage(id As String, version As String) As PackageRecord
        Return New PackageRecord With {
            .package_id = id,
            .version = version,
            .description = "a demo package which is only used to check the sitemap",
            .authors = "xdoc",
            .tags = "demo sitemap",
            .project_url = "",
            .license = "MIT",
            .dependencies = "",
            .downloads = 0,
            .size = 2048,
            .sha256 = "",
            .published = Date.UtcNow,
            .listed = True
        }
    End Function
End Module
