Imports System
Imports System.Collections.Generic
Imports System.Linq
Imports Autodesk.Revit.DB
Imports Autodesk.Revit.DB.Mechanical
Imports Autodesk.Revit.DB.Plumbing
Imports Autodesk.Revit.DB.Electrical
Imports Autodesk.Revit.UI
Imports Infrastructure   ' ElementIdCompat.IntValue / FromInt 사용


Namespace Services

    Public Class ConnectorDiagnosticsService

        ' === 디버그 로그 (호출자가 읽음) ===
        Public Shared Property LastDebug As List(Of String)
        Private Shared Sub Log(msg As String)
            If LastDebug Is Nothing Then LastDebug = New List(Of String)()
            LastDebug.Add($"{DateTime.Now:HH\:mm\:ss.fff} {msg}")
        End Sub

        ' 3-인자: tolFt 는 피트 단위 (ft)
        Public Shared Function Run(app As UIApplication, tolFt As Double, param As String) As List(Of Dictionary(Of String, Object))
            LastDebug = New List(Of String)()
            Dim rows As New List(Of Dictionary(Of String, Object))()

            Dim uidoc = app.ActiveUIDocument
            If uidoc Is Nothing OrElse uidoc.Document Is Nothing Then
                Log("ActiveUIDocument 없음")
                Return rows
            End If
            Dim doc = uidoc.Document
            Log($"시작 tolFt={tolFt:0.###}, param='{param}'")

            ' 1) 커넥터 수집
            Dim items = CollectAllConnectors(doc)
            Log($"커넥터 수집 완료: total={items.Count}")

            If items.Count = 0 Then
                Log("items=0 → 종료")
                Return rows
            End If

            ' 2) tol 기반 버킷
            Dim cell As Double = Math.Max(tolFt, 0.000001)
            Dim buckets = BuildBuckets(items, cell)
            Log($"버킷: cell={cell:0.###}ft, bucketCount={buckets.Count}")

            ' 3) 후보 비교
            Dim tol2 As Double = tolFt * tolFt
            Dim seen As New HashSet(Of String)(StringComparer.Ordinal)
            Dim scanned As Long = 0, withinTol As Long = 0

            For Each it In items
                Dim neigh = GetNeighborCandidates(buckets, it, cell)
                For Each jt In neigh
                    If it.OwnerId = jt.OwnerId Then Continue For

                    Dim aId = Math.Min(it.OwnerId, jt.OwnerId)
                    Dim bId = Math.Max(it.OwnerId, jt.OwnerId)
                    Dim key = aId.ToString() & "_" & bId.ToString() & "_" & it.IndexHint & "_" & jt.IndexHint
                    If seen.Contains(key) Then Continue For
                    seen.Add(key)

                    scanned += 1
                    Dim diff As XYZ = it.P - jt.P
                    Dim d2 As Double = diff.DotProduct(diff)
                    If d2 > tol2 Then Continue For
                    withinTol += 1

                    rows.Add(BuildRow(it.Owner, jt.Owner, Math.Sqrt(d2) * 12.0, it.Conn, jt.Conn, param))
                Next
            Next

            Log($"스캔: scanned={scanned}, withinTol={withinTol}, rows={rows.Count}")

            ' 4) 결과 0건일 때 Fallback: 실제 연결만 전수 수집
            If rows.Count = 0 Then
                Dim fb As New List(Of Dictionary(Of String, Object))()
                Dim seenPair As New HashSet(Of String)(StringComparer.Ordinal)
                Dim tried As Integer = 0

                For Each it In items
                    tried += 1
                    Dim c = it.Conn
                    If c Is Nothing OrElse Not c.IsConnected Then Continue For
                    Dim refs = c.AllRefs
                    If refs Is Nothing OrElse refs.Size = 0 Then Continue For

                    For Each ro In refs
                        Dim rc As Connector = TryCast(ro, Connector)
                        If rc Is Nothing OrElse rc.Owner Is Nothing Then Continue For
                        If rc.Owner.Id.IntegerValue = it.OwnerId Then Continue For

                        Dim aId = Math.Min(it.OwnerId, rc.Owner.Id.IntegerValue)
                        Dim bId = Math.Max(it.OwnerId, rc.Owner.Id.IntegerValue)
                        Dim key = aId.ToString() & "-" & bId.ToString()
                        If seenPair.Contains(key) Then Continue For
                        seenPair.Add(key)

                        Dim distInch As Double = 0.0
                        Try
                            If rc.Origin IsNot Nothing AndAlso it.P IsNot Nothing Then
                                Dim df As XYZ = it.P - rc.Origin
                                distInch = Math.Sqrt(df.DotProduct(df)) * 12.0
                            End If
                        Catch
                        End Try

                        fb.Add(BuildRow(it.Owner, rc.Owner, distInch, it.Conn, rc, param, True))
                    Next
                Next

                Log($"fallback connected: tried={tried}, pairs={fb.Count}")
                If fb.Count > 0 Then rows = fb
            End If

            ' 5) 정렬
            rows = rows.OrderBy(Function(r) ToDouble(r("Distance (inch)"))) _
                       .ThenBy(Function(r) Convert.ToInt32(r("Id1"))) _
                       .ThenBy(Function(r) Convert.ToInt32(r("Id2"))).ToList()

            If rows.Count > 0 Then
                Dim s = rows(0)
                Log($"샘플: Id1={s("Id1")}, Id2={s("Id2")}, d(in)={s("Distance (inch)")}, type={s("ConnectionType")}, v1='{s("Value1")}', v2='{s("Value2")}', status={s("Status")}")
            Else
                Log("최종 rows=0 (근접도/연결 모두 해당 없음)")
            End If

            Return rows
        End Function

        ' 4-인자: tol 은 unit 기준(mm/inch/ft) → 내부에서 ft 로 환산 후 3-인자 호출
        Public Shared Function Run(app As UIApplication, tol As Double, unit As String, paramName As String) As List(Of Dictionary(Of String, Object))
            Dim tolFt As Double
            If String.Equals(unit, "mm", StringComparison.OrdinalIgnoreCase) Then
                tolFt = tol / 304.8
            ElseIf String.Equals(unit, "inch", StringComparison.OrdinalIgnoreCase) OrElse String.Equals(unit, "in", StringComparison.OrdinalIgnoreCase) Then
                tolFt = tol / 12.0
            Else
                tolFt = tol ' ft 가정
            End If
            Return Run(app, tolFt, paramName)
        End Function

        ' --------- 내부 유틸 ---------

        Private Class ConnItem
            Public Property Owner As Element
            Public Property OwnerId As Integer
            Public Property Conn As Connector
            Public Property P As XYZ
            Public Property IndexHint As Integer
        End Class

        Private Shared Function BuildRow(e1 As Element, e2 As Element, distInch As Double, a As Connector, b As Connector, param As String, Optional forceConnected As Boolean = False) As Dictionary(Of String, Object)
            Dim cat1 As String = If(e1.Category Is Nothing, "", e1.Category.Name)
            Dim cat2 As String = If(e2.Category Is Nothing, "", e2.Category.Name)
            Dim fam1 As String = GetFamilyName(e1)
            Dim fam2 As String = GetFamilyName(e2)
            Dim connType As String = If(forceConnected OrElse IsConnectedTo(a, b), "Connected", "Near")
            Dim v1 As String = ReadParamAsString(e1, param)
            Dim v2 As String = ReadParamAsString(e2, param)
            Dim status As String = If(String.Equals(v1, v2, StringComparison.OrdinalIgnoreCase), "OK", "Mismatch")

            Return New Dictionary(Of String, Object)(StringComparer.Ordinal) From {
                {"Id1", e1.Id.IntegerValue.ToString()},
                {"Id2", e2.Id.IntegerValue.ToString()},
                {"Category1", cat1},
                {"Category2", cat2},
                {"Family1", fam1},
                {"Family2", fam2},
                {"Distance (inch)", FormatNumber(distInch)},
                {"ConnectionType", connType},
                {"ParamName", param},
                {"Value1", v1},
                {"Value2", v2},
                {"Status", status}
            }
        End Function

        Private Shared Function BuildBuckets(items As List(Of ConnItem), cellSizeFt As Double) As Dictionary(Of String, List(Of ConnItem))
            Dim dict As New Dictionary(Of String, List(Of ConnItem))(StringComparer.Ordinal)
            For Each it In items
                Dim key = CellKey(it.P, cellSizeFt)
                Dim list As List(Of ConnItem) = Nothing
                If Not dict.TryGetValue(key, list) Then
                    list = New List(Of ConnItem)()
                    dict(key) = list
                End If
                list.Add(it)
            Next
            Return dict
        End Function

        Private Shared Function CellKey(p As XYZ, s As Double) As String
            Dim ix As Long = CLng(Math.Floor(p.X / s))
            Dim iy As Long = CLng(Math.Floor(p.Y / s))
            Dim iz As Long = CLng(Math.Floor(p.Z / s))
            Return ix.ToString() & "," & iy.ToString() & "," & iz.ToString()
        End Function

        Private Shared Iterator Function GetNeighborCandidates(buckets As Dictionary(Of String, List(Of ConnItem)), it As ConnItem, s As Double) As IEnumerable(Of ConnItem)
            Dim ix As Long = CLng(Math.Floor(it.P.X / s))
            Dim iy As Long = CLng(Math.Floor(it.P.Y / s))
            Dim iz As Long = CLng(Math.Floor(it.P.Z / s))
            For dx = -1 To 1
                For dy = -1 To 1
                    For dz = -1 To 1
                        Dim key = (ix + dx).ToString() & "," & (iy + dy).ToString() & "," & (iz + dz).ToString()
                        Dim list As List(Of ConnItem) = Nothing
                        If buckets.TryGetValue(key, list) Then
                            For Each cand In list
                                If cand IsNot it Then Yield cand
                            Next
                        End If
                    Next
                Next
            Next
        End Function

        Private Shared Function CollectAllConnectors(doc As Document) As List(Of ConnItem)
            Dim list As New List(Of ConnItem)()
            Dim idx As Integer = 0
            Dim mep As Integer = 0, fam As Integer = 0

            Try
                For Each e In New FilteredElementCollector(doc).OfClass(GetType(MEPCurve)).WhereElementIsNotElementType().ToElements()
                    Dim cm = TryGetConnectorManager(e)
                    If cm Is Nothing Then Continue For
                    idx = 0
                    For Each o In cm.Connectors
                        Dim c As Connector = TryCast(o, Connector)
                        If c Is Nothing OrElse c.Origin Is Nothing Then Continue For
                        list.Add(New ConnItem With {.Owner = e, .OwnerId = e.Id.IntegerValue, .Conn = c, .P = c.Origin, .IndexHint = idx})
                        idx += 1 : mep += 1
                    Next
                Next

                For Each e In New FilteredElementCollector(doc).OfClass(GetType(FamilyInstance)).WhereElementIsNotElementType().ToElements()
                    Dim cm = TryGetConnectorManager(e)
                    If cm Is Nothing Then Continue For
                    idx = 0
                    For Each o In cm.Connectors
                        Dim c As Connector = TryCast(o, Connector)
                        If c Is Nothing OrElse c.Origin Is Nothing Then Continue For
                        list.Add(New ConnItem With {.Owner = e, .OwnerId = e.Id.IntegerValue, .Conn = c, .P = c.Origin, .IndexHint = idx})
                        idx += 1 : fam += 1
                    Next
                Next
            Catch
            End Try

            Log($"수집 카운트: MEPCurve={mep}, FamilyInstance={fam}")
            Return list
        End Function

        Private Shared Function TryGetConnectorManager(e As Element) As ConnectorManager
            Try
                If TypeOf e Is MEPCurve Then Return DirectCast(e, MEPCurve).ConnectorManager
                Dim fi = TryCast(e, FamilyInstance)
                If fi IsNot Nothing AndAlso fi.MEPModel IsNot Nothing Then Return fi.MEPModel.ConnectorManager
            Catch
            End Try
            Return Nothing
        End Function

        Private Shared Function IsConnectedTo(a As Connector, b As Connector) As Boolean
            Try
                If a Is Nothing OrElse b Is Nothing Then Return False
                If a.IsConnected AndAlso b.IsConnected Then
                    Dim refs = a.AllRefs
                    If refs IsNot Nothing AndAlso refs.Size > 0 Then
                        For Each ro In refs
                            Dim rc As Connector = TryCast(ro, Connector)
                            If rc Is Nothing Then Continue For
                            If rc Is b Then Return True
                            If rc.Owner IsNot Nothing AndAlso b.Owner IsNot Nothing AndAlso rc.Owner.Id.IntegerValue = b.Owner.Id.IntegerValue Then
                                Return True
                            End If
                        Next
                    End If
                End If
            Catch
            End Try
            Return False
        End Function

        Private Shared Function GetFamilyName(e As Element) As String
            Try
                If TypeOf e Is FamilyInstance Then
                    Dim fi = DirectCast(e, FamilyInstance)
                    If fi.Symbol IsNot Nothing AndAlso fi.Symbol.Family IsNot Nothing Then
                        Return fi.Symbol.Family.Name
                    End If
                Else
                    Dim et = TryCast(e.Document.GetElement(e.GetTypeId()), ElementType)
                    If et IsNot Nothing Then
                        Return et.FamilyName
                    End If
                End If
            Catch
            End Try
            Return ""
        End Function

        Private Shared Function ReadParamAsString(e As Element, paramName As String) As String
            If String.IsNullOrWhiteSpace(paramName) Then Return ""
            Try
                Dim p As Parameter = e.LookupParameter(paramName)
                If p Is Nothing Then
                    For Each pp As Parameter In e.Parameters
                        If String.Equals(pp.Definition.Name, paramName, StringComparison.OrdinalIgnoreCase) Then
                            p = pp : Exit For
                        End If
                    Next
                End If
                If p Is Nothing Then Return ""
                If p.StorageType = StorageType.String Then
                    Return p.AsString()
                Else
                    Return p.AsValueString()
                End If
            Catch
            End Try
            Return ""
        End Function

        Private Shared Function FormatNumber(v As Double) As String
            Return Math.Round(v, 4, MidpointRounding.AwayFromZero).ToString("0.####")
        End Function

        Private Shared Function ToDouble(o As Object) As Double
            Try
                If o Is Nothing Then Return 0.0
                Return Convert.ToDouble(o)
            Catch
                Return 0.0
            End Try
        End Function

    End Class

End Namespace
