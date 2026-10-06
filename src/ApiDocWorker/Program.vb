Imports System.IO
Imports System.Text.Json
Imports Nuget
Imports Readership

''' <summary>
''' the isolated api comment document extraction worker of the nuget server.
''' 
''' the server starts this program as a separate child process for every
''' uploaded package: the package ``lib`` content is unpacked here, the xml
''' comment documents are parsed and the sibling clr assemblies are inspected
''' through the <see cref="MetadataLoadContext"/> (metadata only, no code
''' execution). Because everything happens in this disposable process, a
''' hostile package can neither run managed code in the server process nor
''' exhaust it: the parent kills this process when it exceeds its time budget.
''' 
''' usage: ``ApiDocWorker --nupkg &lt;package.nupkg&gt; --output &lt;result.json&gt;``
''' </summary>
Module Program

    Private ReadOnly JsonOptions As New JsonSerializerOptions With {
        .PropertyNameCaseInsensitive = True
    }

    Function Main(args As String()) As Integer
        Dim nupkg As String = getOption(args, "--nupkg")
        Dim output As String = getOption(args, "--output")

        If String.IsNullOrEmpty(nupkg) OrElse String.IsNullOrEmpty(output) Then
            Call Console.Error.WriteLine("usage: ApiDocWorker --nupkg <package.nupkg> --output <result.json>")
            Return 1
        End If

        If Not File.Exists(nupkg) Then
            Call writeResult(output, New ApiDocWorkerResult With {
                .ok = False,
                .message = $"the package file was not found: {nupkg}"
            })
            Return 2
        End If

        Dim temp As String = Path.Combine(Path.GetTempPath(), "xdoc-apidoc-worker-" & Guid.NewGuid().ToString("N"))
        Dim result As New ApiDocWorkerResult With {.ok = True}

        Try
            Call Directory.CreateDirectory(temp)

            Dim xmlCount As Integer = NupkgReader.ExtractLibComments(nupkg, temp)

            If xmlCount = 0 Then
                ' a package without any xml comment document is totally normal
                result.document = ApiDocDocument.Empty()
            Else
                Dim extracted As ApiDocExtractResult = ApiDoc.Extract(New ApiDocOptions With {
                    .Input = temp,
                    .Clean = False,
                    .ReflectSupplement = True
                })

                If extracted.Warnings IsNot Nothing Then
                    result.warnings.AddRange(extracted.Warnings)
                End If

                result.document = extracted.Document
            End If
        Catch ex As Exception
            result.ok = False
            result.message = ex.Message
        Finally
            Try
                If Directory.Exists(temp) Then
                    Call Directory.Delete(temp, recursive:=True)
                End If
            Catch
            End Try
        End Try

        Call writeResult(output, result)

        Return If(result.ok, 0, 3)
    End Function

    Private Sub writeResult(path As String, result As ApiDocWorkerResult)
        Dim json As String = JsonSerializer.Serialize(result, JsonOptions)
        Call File.WriteAllText(path, json, New Text.UTF8Encoding(encoderShouldEmitUTF8Identifier:=False))
    End Sub

    Private Function getOption(args As String(), name As String) As String
        For i As Integer = 0 To args.Length - 2
            If args(i).Equals(name, StringComparison.OrdinalIgnoreCase) Then
                Return args(i + 1)
            End If
        Next

        Return ""
    End Function
End Module
