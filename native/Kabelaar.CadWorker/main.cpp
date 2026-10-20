#define NOMINMAX
#include <windows.h>
#include <STEPControl_Reader.hxx>
#include <STEPControl_Writer.hxx>
#include <Interface_Static.hxx>
#include <BRepMesh_IncrementalMesh.hxx>
#include <BRep_Tool.hxx>
#include <BRepPrimAPI_MakeBox.hxx>
#include <BRepPrimAPI_MakeCylinder.hxx>
#include <BRepAlgoAPI_Cut.hxx>
#include <BRepAdaptor_Curve.hxx>
#include <GCPnts_QuasiUniformDeflection.hxx>
#include <HLRBRep_Algo.hxx>
#include <HLRBRep_HLRToShape.hxx>
#include <HLRAlgo_Projector.hxx>
#include <TopExp_Explorer.hxx>
#include <TopoDS.hxx>
#include <TopoDS_Face.hxx>
#include <Poly_Triangulation.hxx>
#include <Standard_Failure.hxx>
#include <Standard_Version.hxx>
#include <gp_Ax2.hxx>
#include <filesystem>
#include <fstream>
#include <iomanip>
#include <iostream>
#include <string>
#include <vector>
#include <array>
#include <cmath>
#include <algorithm>

namespace fs = std::filesystem;
struct Point { double x, y, z; };
struct Path { bool hidden; std::vector<Point> points; };
struct View { const char* name; gp_Dir normal; gp_Dir xaxis; };
static std::string utf8(const std::wstring& input)
{
    int n = WideCharToMultiByte(CP_UTF8, 0, input.c_str(), (int)input.size(), nullptr, 0, nullptr, nullptr);
    std::string result(n, 0);
    WideCharToMultiByte(CP_UTF8, 0, input.c_str(), (int)input.size(), result.data(), n, nullptr, nullptr);
    return result;
}
static void point(std::ostream& out, const Point& p, bool twoD = false)
{
    if (!std::isfinite(p.x) || !std::isfinite(p.y) || !std::isfinite(p.z)) throw std::runtime_error("Non-finite CAD coordinate");
    out << "{\"x\":" << p.x << ",\"y\":" << p.y;
    if (!twoD) out << ",\"z\":" << p.z;
    out << "}";
}
static void paths(const TopoDS_Shape& shape, bool hidden, double tolerance, std::vector<Path>& result)
{
    if (shape.IsNull()) return;
    for (TopExp_Explorer explorer(shape, TopAbs_EDGE); explorer.More(); explorer.Next())
    {
        auto edge = TopoDS::Edge(explorer.Current());
        if (BRep_Tool::Degenerated(edge)) continue;
        BRepAdaptor_Curve curve(edge);
        GCPnts_QuasiUniformDeflection samples(curve, tolerance);
        if (!samples.IsDone()) throw std::runtime_error("Could not discretize projected edge");
        if (samples.NbPoints() > 100000) throw std::runtime_error("Projected edge exceeds complexity limit");
        Path path{hidden, {}};
        for (int i = 1; i <= samples.NbPoints(); i++)
        {
            auto p = samples.Value(i);
            path.points.push_back({p.X(), -p.Y(), 0});
        }
        if (path.points.size() >= 2) result.push_back(std::move(path));
        if (result.size() > 200000) throw std::runtime_error("Projection exceeds complexity limit");
    }
}
static void emit(const TopoDS_Shape& shape, const fs::path& output, double tolerance)
{
    BRepMesh_IncrementalMesh mesh(shape, tolerance, false, 0.25, false);
    if (!mesh.IsDone()) throw std::runtime_error("CAD meshing failed");
    std::vector<Point> vertices;
    std::vector<std::array<int, 3>> triangles;
    for (TopExp_Explorer explorer(shape, TopAbs_FACE); explorer.More(); explorer.Next())
    {
        auto face = TopoDS::Face(explorer.Current());
        TopLoc_Location location;
        auto triangulation = BRep_Tool::Triangulation(face, location);
        if (triangulation.IsNull()) continue;
        int base = (int)vertices.size();
        for (int i = 1; i <= triangulation->NbNodes(); i++)
        {
            auto p = triangulation->Node(i).Transformed(location.Transformation());
            vertices.push_back({p.X(), p.Y(), p.Z()});
        }
        for (int i = 1; i <= triangulation->NbTriangles(); i++)
        {
            int a, b, c;
            triangulation->Triangle(i).Get(a, b, c);
            if (face.Orientation() == TopAbs_REVERSED) std::swap(b, c);
            triangles.push_back({base + a - 1, base + b - 1, base + c - 1});
        }
        if (vertices.size() > 2000000 || triangles.size() > 2000000) throw std::runtime_error("STEP model exceeds mesh complexity limit");
    }
    if (triangles.empty()) throw std::runtime_error("STEP model contains no tessellatable faces");
    std::ofstream out(output, std::ios::binary);
    if (!out) throw std::runtime_error("Cannot write STEP result");
    out << std::setprecision(12) << "{\"protocolVersion\":1,\"units\":\"mm\",\"kernelVersion\":\"" << OCC_VERSION_COMPLETE << "\",\"warnings\":[],\"mesh\":{\"vertices\":[";
    for (size_t i = 0; i < vertices.size(); i++) { if (i) out << ','; point(out, vertices[i]); }
    out << "],\"triangles\":[";
    for (size_t i = 0; i < triangles.size(); i++) { if (i) out << ','; out << '[' << triangles[i][0] << ',' << triangles[i][1] << ',' << triangles[i][2] << ']'; }
    out << "]},\"views\":[";
    const View views[] = {
        {"Front", gp_Dir(0,-1,0), gp_Dir(1,0,0)}, {"Back", gp_Dir(0,1,0), gp_Dir(-1,0,0)},
        {"Left", gp_Dir(-1,0,0), gp_Dir(0,-1,0)}, {"Right", gp_Dir(1,0,0), gp_Dir(0,1,0)},
        {"Top", gp_Dir(0,0,1), gp_Dir(1,0,0)}, {"Bottom", gp_Dir(0,0,-1), gp_Dir(1,0,0)}
    };
    for (size_t i = 0; i < 6; i++)
    {
        occ::handle<HLRBRep_Algo> algorithm = new HLRBRep_Algo();
        algorithm->Add(shape);
        algorithm->Projector(HLRAlgo_Projector(gp_Ax2(gp_Pnt(0,0,0), views[i].normal, views[i].xaxis)));
        algorithm->Update();
        algorithm->Hide();
        HLRBRep_HLRToShape projected(algorithm);
        std::vector<Path> lines;
        paths(projected.VCompound(), false, tolerance, lines);
        paths(projected.OutLineVCompound(), false, tolerance, lines);
        paths(projected.HCompound(), true, tolerance, lines);
        paths(projected.OutLineHCompound(), true, tolerance, lines);
        double minx = INFINITY, maxx = -INFINITY, miny = INFINITY, maxy = -INFINITY;
        for (const auto& line : lines) for (auto p : line.points) { minx = std::min(minx,p.x); maxx = std::max(maxx,p.x); miny = std::min(miny,p.y); maxy = std::max(maxy,p.y); }
        if (lines.empty()) throw std::runtime_error("Projection contains no edges");
        if (i) out << ',';
        out << "{\"name\":\"" << views[i].name << "\",\"widthMm\":" << maxx-minx << ",\"heightMm\":" << maxy-miny << ",\"paths\":[";
        for (size_t j=0; j<lines.size(); j++)
        {
            if (j) out << ',';
            out << "{\"closed\":false,\"smooth\":false,\"hidden\":" << (lines[j].hidden ? "true" : "false") << ",\"points\":[";
            for (size_t k=0; k<lines[j].points.size(); k++) { if(k) out << ','; auto p=lines[j].points[k]; p.x-=(minx+maxx)/2; p.y-=(miny+maxy)/2; point(out,p,true); }
            out << "]}";
        }
        out << "]}";
    }
    out << "]}";
    out.flush();
    if (!out.good()) throw std::runtime_error("Failed writing complete CAD result");
}
int wmain(int argc, wchar_t** argv)
{
    try
    {
        std::wstring input, output;
        double tolerance = 0.1;
        int protocol = 0;
        bool fixture = false, fixtureInch = false, fixtureCurved = false;
        for(int i=1;i<argc;i++)
        {
            std::wstring argument = argv[i];
            if (argument == L"--fixture-box") { fixture = true; continue; }
            if (argument == L"--fixture-box-inch") { fixture = true; fixtureInch = true; continue; }
            if (argument == L"--fixture-connector") { fixture = true; fixtureCurved = true; continue; }
            if(i+1>=argc) throw std::runtime_error("Missing argument value");
            if(argument==L"--input") input=argv[++i];
            else if(argument==L"--output") output=argv[++i];
            else if(argument==L"--protocol") protocol=std::stoi(argv[++i]);
            else if(argument==L"--deflection") tolerance=std::stod(argv[++i]);
            else throw std::runtime_error("Unknown argument");
        }
        if(protocol!=1 || output.empty() || (!fixture && input.empty())) throw std::runtime_error("Expected --protocol 1 --input file.step --output result.json");
        if(!std::isfinite(tolerance) || tolerance<0.001 || tolerance>10) throw std::runtime_error("Invalid tessellation tolerance");
        if(fixture)
        {
            STEPControl_Writer writer;
            Interface_Static::SetCVal("write.step.unit", fixtureInch ? "INCH" : "MM");
            auto shape = BRepPrimAPI_MakeBox(fixtureInch ? 25.4 : 10, fixtureInch ? 50.8 : 20, fixtureInch ? 76.2 : 30).Shape();
            if (fixtureCurved)
            {
                shape = BRepAlgoAPI_Cut(shape, BRepPrimAPI_MakeCylinder(gp_Ax2(gp_Pnt(5,5,-1),gp_Dir(0,0,1)), 2, 32).Shape()).Shape();
                shape = BRepAlgoAPI_Cut(shape, BRepPrimAPI_MakeCylinder(gp_Ax2(gp_Pnt(5,15,-1),gp_Dir(0,0,1)), 2, 32).Shape()).Shape();
            }
            writer.Transfer(shape, STEPControl_AsIs);
            if (writer.Write(utf8(output).c_str()) != IFSelect_RetDone) throw std::runtime_error("Fixture write failed");
            return 0;
        }
        if(fs::file_size(input)>256ULL*1024*1024) throw std::runtime_error("STEP input exceeds 256 MB");
        Interface_Static::SetCVal("xstep.cascade.unit", "MM");
        STEPControl_Reader reader;
        if(reader.ReadFile(utf8(input).c_str())!=IFSelect_RetDone) throw std::runtime_error("STEP file is invalid or unsupported");
        reader.SetSystemLengthUnit(1.0);
        if(reader.TransferRoots()==0) throw std::runtime_error("STEP contains no transferable shapes");
        auto shape=reader.OneShape();
        if(shape.IsNull()) throw std::runtime_error("STEP contains no shape");
        emit(shape, fs::path(output), tolerance);
        return 0;
    }
    catch(const Standard_Failure& error) { std::cerr << "Open CASCADE: " << error.what() << '\n'; }
    catch(const std::exception& error) { std::cerr << error.what() << '\n'; }
    catch(...) { std::cerr << "Unexpected CAD import failure\n"; }
    return 2;
}
