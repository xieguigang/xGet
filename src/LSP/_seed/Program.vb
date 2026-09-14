Imports System.Text.Json
Imports Nuget
Imports Readership

''' <summary>
''' temporary, in repo seeder used only to populate a small synthetic nuget api
''' document database so the LSP server can be smoke tested against real data.
''' this folder is removed after testing.
''' </summary>
Module Seed

    Sub Main(args As String())
        Dim dataDir As String = If(args.Length > 0, args(0), "g:/tmp/lsptestdb")
        Dim store As New NugetStore(dataDir)

        Dim t As New ApiDocType()
        t.fullName = "System.Text.StringBuilder"
        t.name = "StringBuilder"
        t.namespaceName = "System.Text"
        t.summary = "A mutable string of characters."
        t.packageId = "Test"
        t.packageVersion = "1.0.0"
        t.members = New List(Of ApiDocMember)()

        Dim append As New ApiDocMember()
        append.kind = "method"
        append.kindChar = "M"c
        append.name = "Append"
        append.declaration = "System.Text.StringBuilder.Append(System.String value)"
        append.summary = "Appends a string to this builder and returns it."
        append.returns = "System.Text.StringBuilder"
        append.parameters = New List(Of ApiDocParam)()
        Dim p As New ApiDocParam()
        p.name = "value"
        p.text = "The string to append."
        append.parameters.Add(p)
        t.members.Add(append)

        Dim length As New ApiDocMember()
        length.kind = "property"
        length.kindChar = "P"c
        length.name = "Length"
        length.declaration = "System.Text.StringBuilder.Length As System.Int32"
        length.summary = "The number of characters currently in the builder."
        length.returns = "System.Int32"
        t.members.Add(length)
        t.memberCountHint = 2

        Dim payload As String = JsonSerializer.Serialize(t)
        Call store.ReplacePackageApiDocs("Test", "1.0.0", "System.Text.StringBuilder", "StringBuilder", "System.Text", "A mutable string of characters.", 2, payload)

        Console.WriteLine("seeded System.Text.StringBuilder into " & dataDir)
    End Sub
End Module
