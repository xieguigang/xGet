Imports System.Collections.Generic
Imports System.IO
Imports Nuget

''' <summary>
''' store the api comment documents of one nupkg file into a server database:
''' the very same rows which the api document daemon writes after an upload.
''' 
''' it exists so that the static page generation can be checked on a local
''' machine without a configured mail server (the registration / upload flow
''' needs one). run it while the server is stopped, then start the server: the
''' reconciliation of the daemon fills the static page cache.
''' 
''' usage: test seeddocs [nupkg] [data-dir]
''' </summary>
Module SeedDocsTest

    Public Function Run(args As String()) As Integer
        Dim nupkg As String = argAt(args, 0, "G:\xDoc\dist\bin\Nuget.1.0.0.nupkg")
        Dim data As String = argAt(args, 1, "G:\xDoc\dist\data")

        Call Console.WriteLine($"nupkg: {nupkg}")
        Call Console.WriteLine($"data : {data}")

        If Not File.Exists(nupkg) Then
            Call Console.WriteLine("the nupkg file was not found.")
            Return 2
        End If

        Dim metadata As NupkgMetadata = NupkgReader.ReadMetadata(nupkg)
        Dim warnings As New List(Of String)
        Dim document As Readership.ApiDocDocument = PackageApiDocs.ExtractInProcess(nupkg, warnings)

        If document Is Nothing Then
            Call Console.WriteLine("the api comment documents could not be extracted.")
            Return 2
        End If

        Dim records As List(Of PackageApiDocRecord) = PackageApiDocs.ToRecords(document, metadata.Id, metadata.Version)

        If records.Count = 0 Then
            Call Console.WriteLine("the package ships no api comment document.")
            Return 2
        End If

        Dim db As String = Path.Combine(data, "db")
        Dim store As New NugetStore(db)

        Call store.ReplacePackageApiDocs(metadata.Id, metadata.Version, records)

        Call Console.WriteLine($"stored: {records.Count} type document(s) of {metadata.Id} {metadata.Version} into {db}")

        For Each message As String In warnings
            Call Console.WriteLine($"  warning: {message}")
        Next

        Return 0
    End Function

    Private Function argAt(args As String(), index As Integer, fallback As String) As String
        If args IsNot Nothing AndAlso args.Length > index AndAlso Not String.IsNullOrWhiteSpace(args(index)) Then
            Return args(index)
        End If

        Return fallback
    End Function
End Module
