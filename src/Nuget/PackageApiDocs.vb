Imports System.IO
Imports System.Text.Json
Imports Readership

''' <summary>
''' Extract the api comment documents of one uploaded nuget package: the xml
''' comment documents and the sibling clr assemblies of the ``lib`` folder are
''' unpacked to a temporary directory, the document model is extracted by
''' <see cref="ApiDoc.Extract"/> (with the reflection supplement enabled) and
''' every type is serialized into one <see cref="PackageApiDocRecord"/> row.
''' </summary>
Public Module PackageApiDocs

    Private ReadOnly JsonOptions As New JsonSerializerOptions With {
        .PropertyNameCaseInsensitive = True
    }

    ''' <summary>
    ''' extract the per type api document rows of one nupkg file. The extraction
    ''' is best effort: a failure is only reported as a warning and an empty list
    ''' is returned, so that the package itself is still published.
    ''' </summary>
    ''' <param name="nupkgPath">the physical nupkg path.</param>
    ''' <param name="packageId">the package id.</param>
    ''' <param name="version">the package version.</param>
    ''' <param name="warnings">the warning collector (may be <c>Nothing</c>).</param>
    ''' <returns>the per type document rows.</returns>
    Public Function Extract(nupkgPath As String, packageId As String, version As String,
                            Optional warnings As List(Of String) = Nothing) As List(Of PackageApiDocRecord)

        Dim result As New List(Of PackageApiDocRecord)

        If String.IsNullOrEmpty(nupkgPath) OrElse Not File.Exists(nupkgPath) Then
            Call addWarning(warnings, $"the package file was not found: {nupkgPath}")
            Return result
        End If

        Dim temp As String = Path.Combine(Path.GetTempPath(), "xdoc-apidoc-" & Guid.NewGuid().ToString("N"))

        Try
            Call Directory.CreateDirectory(temp)

            Dim xmlCount As Integer = NupkgReader.ExtractLibComments(nupkgPath, temp)

            If xmlCount = 0 Then
                ' a package without any xml comment document is totally normal
                Return result
            End If

            Dim extracted As ApiDocExtractResult = ApiDoc.Extract(New ApiDocOptions With {
                .Input = temp,
                .Clean = False,
                .ReflectSupplement = True
            })

            If extracted.Warnings IsNot Nothing AndAlso warnings IsNot Nothing Then
                Call warnings.AddRange(extracted.Warnings)
            End If

            If extracted.Document Is Nothing Then
                Return result
            End If

            result = toRecords(extracted.Document, packageId, version)

            Return result
        Catch ex As Exception
            Call addWarning(warnings, $"{packageId} {version}: api document extraction failed: {ex.Message}")
            Return New List(Of PackageApiDocRecord)
        Finally
            Try
                If Directory.Exists(temp) Then
                    Call Directory.Delete(temp, recursive:=True)
                End If
            Catch
            End Try
        End Try
    End Function

    ''' <summary>
    ''' convert the extracted document into the per type storage rows.
    ''' </summary>
    ''' <param name="document"></param>
    ''' <param name="packageId"></param>
    ''' <param name="version"></param>
    ''' <returns></returns>
    Private Function toRecords(document As ApiDocDocument, packageId As String, version As String) As List(Of PackageApiDocRecord)
        Dim result As New List(Of PackageApiDocRecord)
        Dim namespaceSummaries As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)

        For Each ns As ApiDocNamespace In If(document.namespaces, New List(Of ApiDocNamespace))
            If Not String.IsNullOrEmpty(ns.name) Then
                namespaceSummaries(ns.name) = ns.summary
            End If
        Next

        For Each t As ApiDocType In If(document.types, New List(Of ApiDocType))
            If t Is Nothing OrElse String.IsNullOrEmpty(t.fullName) Then
                Continue For
            End If

            Dim namespaceSummary As String = Nothing

            If Not String.IsNullOrEmpty(t.namespaceName) Then
                Call namespaceSummaries.TryGetValue(t.namespaceName, namespaceSummary)
            End If

            t.packageId = packageId
            t.packageVersion = version
            t.memberCountHint = t.MemberCount()

            Call result.Add(New PackageApiDocRecord With {
                .package_id = packageId,
                .version = version,
                .namespace_name = t.namespaceName,
                .namespace_summary = If(namespaceSummary, ""),
                .type_fullname = t.fullName,
                .type_name = t.name,
                .summary = t.summary,
                .member_count = t.memberCountHint,
                .payload = JsonSerializer.Serialize(t, JsonOptions)
            })
        Next

        Return result
    End Function

    Private Sub addWarning(warnings As List(Of String), message As String)
        If warnings IsNot Nothing Then
            Call warnings.Add(message)
        End If
    End Sub
End Module
