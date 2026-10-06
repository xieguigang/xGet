Imports System.Diagnostics
Imports System.IO
Imports System.Text.Json
Imports Readership

''' <summary>
''' Extract the api comment documents of one uploaded nuget package. The
''' extraction runs inside the isolated ``ApiDocWorker`` child process: the
''' worker unpacks the package ``lib`` folder, parses the xml comment documents
''' and inspects the sibling clr assemblies through the metadata-only
''' <see cref="System.Reflection.MetadataLoadContext"/>. A hard timeout kills
''' the worker, and when the worker executable is not deployed the extraction
''' falls back to the (also metadata only) in-process path.
''' 
''' Every type is serialized into one <see cref="PackageApiDocRecord"/> row.
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
    ''' <param name="workerTimeoutSeconds">
    ''' the hard timeout of the ``ApiDocWorker`` child process in seconds.
    ''' </param>
    ''' <returns>the per type document rows.</returns>
    Public Function Extract(nupkgPath As String, packageId As String, version As String,
                            Optional warnings As List(Of String) = Nothing,
                            Optional workerTimeoutSeconds As Integer = 120) As List(Of PackageApiDocRecord)

        Dim result As New List(Of PackageApiDocRecord)

        If String.IsNullOrEmpty(nupkgPath) OrElse Not File.Exists(nupkgPath) Then
            Call addWarning(warnings, $"the package file was not found: {nupkgPath}")
            Return result
        End If

        Dim document As ApiDocDocument = runWorker(nupkgPath, workerTimeoutSeconds, warnings)

        If document Is Nothing Then
            document = extractInProcess(nupkgPath, warnings)
        End If

        If document Is Nothing Then
            Return result
        End If

        result = toRecords(document, packageId, version)

        Return result
    End Function

    ''' <summary>
    ''' run the ``ApiDocWorker`` child process. Returns the extracted document,
    ''' or <c>Nothing</c> when the worker is not deployed, has timed out or has
    ''' failed (in which case the caller falls back to the in-process path).
    ''' </summary>
    Private Function runWorker(nupkgPath As String, timeoutSeconds As Integer, warnings As List(Of String)) As ApiDocDocument
        Dim exe As String = Path.Combine(AppContext.BaseDirectory, "ApiDocWorker.exe")
        Dim dll As String = Path.Combine(AppContext.BaseDirectory, "ApiDocWorker.dll")
        Dim psi As ProcessStartInfo

        If File.Exists(exe) Then
            psi = New ProcessStartInfo(exe)
        ElseIf File.Exists(dll) Then
            psi = New ProcessStartInfo("dotnet", $"""{dll}""")
        Else
            Call addWarning(warnings, "the ApiDocWorker executable was not found next to the server, " &
                                      "the api document extraction runs inside the server process")
            Return Nothing
        End If

        Dim outputFile As String = Path.Combine(Path.GetTempPath(), "xdoc-apidoc-result-" & Guid.NewGuid().ToString("N") & ".json")
        Dim watch As Stopwatch = Stopwatch.StartNew()

        psi.Arguments = $"--nupkg ""{nupkgPath}"" --output ""{outputFile}"""
        psi.UseShellExecute = False
        psi.CreateNoWindow = True
        psi.RedirectStandardOutput = True
        psi.RedirectStandardError = True

        Try
            Using process As Process = Process.Start(psi)
                If process Is Nothing Then
                    Call addWarning(warnings, "the ApiDocWorker process could not be started")
                    Return Nothing
                End If

                ' drain the pipes after the wait: the worker only writes to
                ' stderr on a usage error, so a pipe deadlock is not expected.
                If Not process.WaitForExit(timeoutSeconds * 1000) Then
                    Try
                        Call process.Kill(entireProcessTree:=True)
                        Call process.WaitForExit(5000)
                    Catch
                    End Try

                    Call addWarning(warnings, $"the ApiDocWorker process was killed after the {timeoutSeconds}s timeout")
                    Return Nothing
                End If

                Dim [error] As String = process.StandardError.ReadToEnd()

                If process.ExitCode <> 0 OrElse Not File.Exists(outputFile) Then
                    Call addWarning(warnings, $"the ApiDocWorker process failed (exit code {process.ExitCode}): " &
                                              $"{[error].Trim()} - falling back to the in-process extraction")
                    Return Nothing
                End If

                Dim json As String = File.ReadAllText(outputFile)
                Dim result As ApiDocWorkerResult = JsonSerializer.Deserialize(Of ApiDocWorkerResult)(json, JsonOptions)

                If result Is Nothing Then
                    Call addWarning(warnings, "the ApiDocWorker result could not be decoded")
                    Return Nothing
                End If

                If result.warnings IsNot Nothing Then
                    Call warnings.AddRange(result.warnings)
                End If

                If Not result.ok Then
                    Call addWarning(warnings, $"the ApiDocWorker reported a fatal error: {result.message}")
                    Return Nothing
                End If

                Call $"api document worker finished in {watch.ElapsedMilliseconds}ms".debug()
                Return result.document
            End Using
        Catch ex As Exception
            Call addWarning(warnings, $"the ApiDocWorker process failed: {ex.Message}")
            Return Nothing
        Finally
            Try
                If File.Exists(outputFile) Then
                    File.Delete(outputFile)
                End If
            Catch
            End Try
        End Try
    End Function

    ''' <summary>
    ''' the in-process fallback: unpack the package ``lib`` folder and extract
    ''' the document model through the metadata-only reflection supplement.
    ''' </summary>
    Private Function extractInProcess(nupkgPath As String, warnings As List(Of String)) As ApiDocDocument
        Dim temp As String = Path.Combine(Path.GetTempPath(), "xdoc-apidoc-" & Guid.NewGuid().ToString("N"))

        Try
            Call Directory.CreateDirectory(temp)

            Dim xmlCount As Integer = NupkgReader.ExtractLibComments(nupkgPath, temp)

            If xmlCount = 0 Then
                ' a package without any xml comment document is totally normal
                Return Nothing
            End If

            Dim extracted As ApiDocExtractResult = ApiDoc.Extract(New ApiDocOptions With {
                .Input = temp,
                .Clean = False,
                .ReflectSupplement = True
            })

            If extracted.Warnings IsNot Nothing AndAlso warnings IsNot Nothing Then
                Call warnings.AddRange(extracted.Warnings)
            End If

            Return extracted.Document
        Catch ex As Exception
            Call addWarning(warnings, $"api document extraction failed: {ex.Message}")
            Return Nothing
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
