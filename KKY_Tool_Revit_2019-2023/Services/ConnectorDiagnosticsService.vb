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

            ' 1) 커넥터 있는 요소 수집 (Command 버전 기준)
            Dim elems = CollectElementsWithConnectors(doc)
            Log($"수집 요소: {elems.Count}")

            If elems.Count = 0 Then
                Log("커넥터를 가진 요소가 없습니다.")
                Return rows
            End If

            ' 요소별 커넥터 매핑
            Dim elemConns As New Dictionary(Of Integer, List(Of Connector))()
            For Each el In elems
                elemConns(el.Id.IntegerValue) = GetConnectors(el)
            Next

            ' 모든 커넥터 좌표 버킷 구성 (1ft 셀)
            Dim allConnPoints As New List(Of Tuple(Of Integer, XYZ, Connector))()
            For Each kv In elemConns
                For Each c In kv.Value
                    allConnPoints.Add(Tuple.Create(kv.Key, c.Origin, c))
                Next
            Next
            Dim buckets = BuildGrid(allConnPoints)
            Log($"버킷 수: {buckets.Count}")

            ' 후보 비교 (Command 로직)
            Dim seenPairs As New HashSet(Of String)(StringComparer.Ordinal)

            For Each el In elems
                Dim baseId = el.Id.IntegerValue
                Dim conns = elemConns(baseId)
                For Each c In conns
                    Dim found As Element = Nothing
                    Dim distFt As Double = 0
                    Dim connType As String = ""

                    ' 1) 실제 연결
                    If c.IsConnected Then
                        For Each r As Connector In c.AllRefs.Cast(Of Connector)()
                            If r?.Owner Is Nothing Then Continue For
                            If r.Owner.Id.IntegerValue = baseId Then Continue For
                            If TypeOf r.Owner Is MEPSystem Then Continue For
                            found = r.Owner
                            connType = "Physical(커넥터 연결 됨)"
                            Exit For
                        Next
                    End If

                    ' 2) 근접 후보
                    If found Is Nothing Then
                        Dim key = BucketKey(c.Origin)
                        For dx = -1 To 1
                            For dy = -1 To 1
                                For dz = -1 To 1
                                    Dim nbKey = Tuple.Create(key.Item1 + dx, key.Item2 + dy, key.Item3 + dz)
                                    If buckets.ContainsKey(nbKey) Then
                                        For Each nb In buckets(nbKey)
                                            Dim otherId = nb.Item1
                                            If otherId = baseId Then Continue For

                                            Dim d = c.Origin.DistanceTo(nb.Item2)
                                            If d <= tolFt Then
                                                found = doc.GetElement(New ElementId(otherId))
                                                distFt = d
                                                connType = "Proximity(커넥터 연결 필요)"
                                                Exit For
                                            End If
                                        Next
                                    End If
                                    If found IsNot Nothing Then Exit For
                                Next
                                If found IsNot Nothing Then Exit For
                            Next
                            If found IsNot Nothing Then Exit For
                        Next
                    End If

                    If String.IsNullOrEmpty(connType) Then connType = "연결 대상 객체 없음"

                    Dim distInch As Double = Math.Round(distFt * 12.0, 2)
                    Dim v1 = GetParamValue(el, param)
                    Dim v2 As String = If(found IsNot Nothing, GetParamValue(found, param), "N/A")
                    Dim status = If(found Is Nothing, "연결 대상 객체 없음", If(v1 = v2, "Match", "Mismatch"))

                    Dim id1Val = baseId
                    Dim id2Val = If(found IsNot Nothing, found.Id.IntegerValue, 0)
                    Dim pairKey As String = If(id1Val <= id2Val, $"{id1Val}_{id2Val}", $"{id2Val}_{id1Val}")
                    If seenPairs.Contains(pairKey) Then Continue For
                    seenPairs.Add(pairKey)

                    rows.Add(BuildRow(el, found, distInch, connType, param, v1, v2, status))
                Next
            Next

            ' 정렬 및 샘플 로그
            rows = rows.OrderBy(Function(r) ToDouble(r("Distance (inch)"))) _
                       .ThenBy(Function(r) Convert.ToInt32(r("Id1"))) _
                       .ThenBy(Function(r) Convert.ToInt32(r("Id2"))) _
                       .ToList()

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

        Private Shared Function BuildRow(e1 As Element, e2 As Element, distInch As Double, connType As String, param As String, v1 As String, v2 As String, status As String) As Dictionary(Of String, Object)
            Dim cat1 As String = If(e1?.Category Is Nothing, "", e1.Category.Name)
            Dim cat2 As String = If(e2?.Category Is Nothing, "", e2.Category.Name)
            Dim fam1 As String = GetFamilyName(e1)
            Dim fam2 As String = GetFamilyName(e2)

            Return New Dictionary(Of String, Object)(StringComparer.Ordinal) From {
                {"Id1", If(e1 IsNot Nothing, e1.Id.IntegerValue.ToString(), "0")},
                {"Id2", If(e2 IsNot Nothing, e2.Id.IntegerValue.ToString(), "0")},
                {"Category1", cat1},
                {"Category2", cat2},
                {"Family1", fam1},
                {"Family2", fam2},
                {"Distance (inch)", distInch},
                {"ConnectionType", connType},
                {"ParamName", param},
                {"Value1", v1},
                {"Value2", v2},
                {"Status", status}
            }
        End Function

        Private Shared Function CollectElementsWithConnectors(doc As Document) As List(Of Element)
            Dim elems As New List(Of Element)()

            For Each fi As FamilyInstance In New FilteredElementCollector(doc).OfClass(GetType(FamilyInstance))
                Try
                    If fi.MEPModel IsNot Nothing AndAlso fi.MEPModel.ConnectorManager IsNot Nothing AndAlso fi.MEPModel.ConnectorManager.Connectors IsNot Nothing AndAlso fi.MEPModel.ConnectorManager.Connectors.Cast(Of Connector)().Any() Then
                        elems.Add(fi)
                    End If
                Catch
                End Try
            Next

            Dim cats = New BuiltInCategory() {
                BuiltInCategory.OST_PipeCurves, BuiltInCategory.OST_DuctCurves, BuiltInCategory.OST_CableTray, BuiltInCategory.OST_Conduit,
                BuiltInCategory.OST_PipeFitting, BuiltInCategory.OST_DuctFitting, BuiltInCategory.OST_CableTrayFitting, BuiltInCategory.OST_ConduitFitting,
                BuiltInCategory.OST_PipeAccessory, BuiltInCategory.OST_DuctAccessory
            }

            For Each cat In cats
                For Each el As Element In New FilteredElementCollector(doc).OfCategory(cat).WhereElementIsNotElementType()
                    If HasConnectors(el) Then elems.Add(el)
                Next
            Next

            Return elems.Distinct().ToList()
        End Function

        Private Shared Function HasConnectors(el As Element) As Boolean
            Try
                Dim fi = TryCast(el, FamilyInstance)
                If fi?.MEPModel IsNot Nothing AndAlso fi.MEPModel.ConnectorManager?.Connectors Is Not Nothing Then
                    Return fi.MEPModel.ConnectorManager.Connectors.Cast(Of Connector)().Any()
                End If

                Dim mc = TryCast(el, MEPCurve)
                If mc?.ConnectorManager?.Connectors Is Not Nothing Then
                    Return mc.ConnectorManager.Connectors.Cast(Of Connector)().Any()
                End If
            Catch
            End Try
            Return False
        End Function

        Private Shared Function GetConnectors(el As Element) As List(Of Connector)
            Try
                Dim fi = TryCast(el, FamilyInstance)
                If fi?.MEPModel IsNot Nothing AndAlso fi.MEPModel.ConnectorManager Is Not Nothing Then
                    Return fi.MEPModel.ConnectorManager.Connectors.Cast(Of Connector)().ToList()
                End If

                Dim mc = TryCast(el, MEPCurve)
                If mc?.ConnectorManager Is Not Nothing Then
                    Return mc.ConnectorManager.Connectors.Cast(Of Connector)().ToList()
                End If
            Catch
            End Try
            Return New List(Of Connector)()
        End Function

        Private Shared Function GetFamilyName(e As Element) As String
            Try
                If TypeOf e Is FamilyInstance Then
                    Dim fi = DirectCast(e, FamilyInstance)
                    If fi.Symbol IsNot Nothing AndAlso fi.Symbol.Family Is Not Nothing Then
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

        Private Shared Function GetParamValue(el As Element, name As String) As String
            If el Is Nothing OrElse String.IsNullOrWhiteSpace(name) Then Return "N/A"
            Dim p = el.LookupParameter(name)
            If p Is Nothing OrElse Not p.HasValue Then Return "N/A"

            Select Case p.StorageType
                Case StorageType.[String]
                    Return p.AsString()
                Case StorageType.Double
                    Return p.AsDouble().ToString()
                Case StorageType.Integer
                    Return p.AsInteger().ToString()
                Case Else
                    Return p.AsValueString()
            End Select
        End Function

        Private Shared Function BuildGrid(items As List(Of Tuple(Of Integer, XYZ, Connector))) As Dictionary(Of Tuple(Of Integer, Integer, Integer), List(Of Tuple(Of Integer, XYZ, Connector)))
            Dim grid As New Dictionary(Of Tuple(Of Integer, Integer, Integer), List(Of Tuple(Of Integer, XYZ, Connector)))()
            For Each tup In items
                Dim key = BucketKey(tup.Item2)
                If Not grid.ContainsKey(key) Then
                    grid(key) = New List(Of Tuple(Of Integer, XYZ, Connector))()
                End If
                grid(key).Add(tup)
            Next
            Return grid
        End Function

        Private Shared Function BucketKey(p As XYZ) As Tuple(Of Integer, Integer, Integer)
            Return Tuple.Create(CInt(Math.Floor(p.X)), CInt(Math.Floor(p.Y)), CInt(Math.Floor(p.Z)))
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
