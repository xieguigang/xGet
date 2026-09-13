Imports Microsoft.VisualBasic.ApplicationServices.Development.XmlDoc.Assembly
Imports Microsoft.VisualBasic.ApplicationServices.Development.XmlDoc.Serialization
Imports Microsoft.VisualBasic.Linq

''' <summary>
''' The data extract stage of the api reference document generator: it converts
''' the live xml comment document model (see <see cref="ProjectSpace"/>) into
''' the serializable <see cref="ApiDocDocument"/> model, so that the document
''' data and the page rendering become two independent steps.
''' </summary>
Friend Module DocExtractor

    ''' <summary>
    ''' build the serializable document model from the loaded xml comment
    ''' document projects.
    ''' </summary>
    ''' <param name="space"></param>
    ''' <param name="warnings"></param>
    ''' <returns></returns>
    Public Function BuildDocument(space As IEnumerable(Of Project), warnings As List(Of String)) As ApiDocDocument
        Dim document As ApiDocDocument = ApiDocDocument.Empty()

        If space Is Nothing Then
            Return document
        End If

        For Each proj As Project In space _
            .Where(Function(p) p IsNot Nothing) _
            .OrderBy(Function(p) p.Name)

            If Not document.assemblies.Contains(proj.Name) Then
                Call document.assemblies.Add(proj.Name)
            End If

            For Each ns As ProjectNamespace In proj.Namespaces _
                .Where(Function(n) n IsNot Nothing) _
                .OrderBy(Function(n) If(n.fullName, ""))

                Dim namespaceName$ = If(ns.fullName, "")
                Dim nsEntry As New ApiDocNamespace With {
                    .name = namespaceName,
                    .project = proj.Name
                }
                Dim namespaceDoc As New List(Of String)

                For Each t As ProjectType In ns.Types _
                    .Where(Function(x) x IsNot Nothing) _
                    .OrderBy(Function(x) x.Name)

                    ' the NamespaceDoc magic type only carries the documentation of
                    ' its namespace, so its comment text is merged into the namespace
                    ' instead of being generated as an independent type page.
                    If String.Equals(t.Name, APIExtensions.NamespaceDoc, StringComparison.Ordinal) Then
                        If Not String.IsNullOrWhiteSpace(t.Summary) Then
                            Call namespaceDoc.Add(t.Summary.Trim())
                        End If

                        If Not String.IsNullOrWhiteSpace(t.Remarks) Then
                            Call namespaceDoc.Add(t.Remarks.Trim())
                        End If

                        Continue For
                    End If

                    Dim fullName$ = If(String.IsNullOrEmpty(namespaceName), t.Name, namespaceName & "." & t.Name)
                    Dim typeEntry As New ApiDocType With {
                        .name = t.Name,
                        .fullName = fullName,
                        .project = proj.Name,
                        .namespaceName = namespaceName,
                        .summary = t.Summary,
                        .remarks = t.Remarks,
                        .typeParams = ToParams(t.TypeParams),
                        .members = New List(Of ApiDocMember)
                    }

                    Call buildMembers(typeEntry, t)
                    Call AssignMemberLayout(typeEntry)
                    Call document.types.Add(typeEntry)
                Next

                nsEntry.summary = namespaceDoc.Distinct().JoinBy(vbLf & vbLf)
                Call document.namespaces.Add(nsEntry)
            Next
        Next

        Return document
    End Function

    ''' <summary>
    ''' compute the html anchor and the overload index of every member of the
    ''' given type. The members are grouped by their kind and their name, and the
    ''' overloads are ordered by their declare text, so that the anchors are
    ''' stable and independent of the member collection order.
    ''' </summary>
    ''' <param name="type"></param>
    Public Sub AssignMemberLayout(type As ApiDocType)
        If type.members Is Nothing Then
            type.members = New List(Of ApiDocMember)
        End If

        For Each kind As Char In "MPFE"
            Dim groups = type.members _
                .Where(Function(m) m IsNot Nothing AndAlso String.Equals(m.kindChar, kind.ToString, StringComparison.Ordinal)) _
                .GroupBy(Function(m) If(m.name, "")) _
                .OrderBy(Function(x) x.Key)

            For Each g In groups
                Dim overloadList = g.OrderBy(Function(m) If(m.declaration, "")).ToList

                For i As Integer = 1 To overloadList.Count
                    Dim m As ApiDocMember = overloadList(i - 1)
                    m.anchor = "member-" & Char.ToLowerInvariant(kind) & "-" & DocNaming.Slug(g.Key) &
                        If(i = 1, "", "-" & i.ToString)
                    m.overloadIndex = i - 1
                Next
            Next
        Next
    End Sub

    Private Sub buildMembers(typeEntry As ApiDocType, src As ProjectType)
        Call addMembers(typeEntry, "M"c, "method", src.AllMethods)
        Call addMembers(typeEntry, "P"c, "property", src.AllProperties)
        Call addMembers(typeEntry, "F"c, "field", src.AllFields)
        Call addMembers(typeEntry, "E"c, "event", src.AllEvents)
    End Sub

    Private Sub addMembers(typeEntry As ApiDocType, kindChar As Char, kind As String, members As IEnumerable(Of ProjectMember))
        If members Is Nothing Then
            Return
        End If

        For Each m As ProjectMember In members
            If m Is Nothing Then
                Continue For
            End If

            Call typeEntry.members.Add(New ApiDocMember With {
                .kindChar = kindChar.ToString,
                .kind = kind,
                .name = If(m.Name, ""),
                .declaration = If(m.[Declare], m.Name),
                .summary = m.Summary,
                .remarks = m.Remarks,
                .returns = m.Returns,
                .example = m.example,
                .parameters = ToParams(m.Params),
                .typeParams = ToParams(m.TypeParams)
            })
        Next
    End Sub

    ''' <summary>
    ''' convert the core xml document parameter array into the serializable
    ''' parameter list.
    ''' </summary>
    ''' <param name="items"></param>
    ''' <returns></returns>
    Private Function ToParams(items As IEnumerable(Of param)) As List(Of ApiDocParam)
        Dim list As New List(Of ApiDocParam)

        If items Is Nothing Then
            Return list
        End If

        For Each item As param In items
            If item Is Nothing Then
                Continue For
            End If

            Call list.Add(New ApiDocParam With {
                .name = item.name,
                .text = item.text
            })
        Next

        Return list
    End Function
End Module
