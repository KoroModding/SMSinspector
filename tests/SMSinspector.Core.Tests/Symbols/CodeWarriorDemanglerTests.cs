using SMSinspector.Core.Symbols;

namespace SMSinspector.Core.Tests.Symbols;

// Every mangled name here is invented for the tests. Length prefixes count the
// characters of the name that follows, template arguments included.
public class CodeWarriorDemanglerTests
{
    [Theory]
    [InlineData("update__9TFooActorFv", "TFooActor::update()")]
    [InlineData("draw__Q23Gfx7TCanvasCFi", "Gfx::TCanvas::draw(int) const")]
    [InlineData("__ct__9TFooActorFv", "TFooActor::TFooActor()")]
    [InlineData("__dt__Q23Gfx7TCanvasFv", "Gfx::TCanvas::~TCanvas()")]
    [InlineData("helper__Fi", "helper(int)")]
    [InlineData("sCount__9TFooActor", "TFooActor::sCount")]
    [InlineData("move__9TFooActorFRCQ23Gfx7TCanvasPUcUlf", "TFooActor::move(const Gfx::TCanvas&, unsigned char*, unsigned long, float)")]
    [InlineData("reset___9TFooActorFv", "TFooActor::reset_()")]
    [InlineData("load__9TFooActorFPA4_fRA2_A3_Ci", "TFooActor::load(float[4]*, const int[2][3]&)")]
    [InlineData("setHook__9TFooActorFPFi_v", "TFooActor::setHook(void(int)*)")]
    [InlineData("push__16TBox<9TFooActor>FP9TFooActor", "TBox<TFooActor>::push(TFooActor*)")]
    [InlineData("@16@__dt__9TFooActorFv", "TFooActor::~TFooActor() [thunk, this adjusted by 16]")]
    public void Demangles_functions_and_members(string mangled, string expected)
    {
        Assert.Equal(expected, CodeWarriorDemangler.Demangle(mangled));
    }

    [Theory]
    [InlineData("memset")]
    [InlineData("__start")]
    [InlineData("@123")]
    [InlineData("lbl_80001234")]
    [InlineData("foo__99TTooShort")]
    [InlineData("foo__9TFooActorFz")]
    public void Leaves_unparseable_names_unchanged(string name)
    {
        Assert.Equal(name, CodeWarriorDemangler.Demangle(name));
    }

    [Theory]
    [InlineData("__vt__9TFooActor", "TFooActor")]
    [InlineData("__vt__Q23Gfx7TCanvas", "Gfx::TCanvas")]
    [InlineData("__vt__16TBox<9TFooActor>", "TBox<TFooActor>")]
    [InlineData("__vt__23TPair<Q23Gfx7TCanvas,f>", "TPair<Gfx::TCanvas, float>")]
    [InlineData("__vt__20TStack<PC9TFooActor>", "TStack<const TFooActor*>")]
    [InlineData("__vt__24TBox<16TBox<9TFooActor>>", "TBox<TBox<TFooActor>>")]
    [InlineData("__vt__Q23Gfx17TList<9TFooActor>", "Gfx::TList<TFooActor>")]
    [InlineData("__vt__7TArr<4>", "TArr<4>")]
    [InlineData("__vt__13TFlags<Uc,-1>", "TFlags<unsigned char, -1>")]
    public void Vtable_symbols_give_their_class(string symbol, string expected)
    {
        Assert.True(CodeWarriorDemangler.TryGetVtableClass(symbol, out var className));
        Assert.Equal(expected, className);
    }

    [Theory]
    [InlineData("update__9TFooActorFv")]
    [InlineData("__vt__9TFooActorX")]
    [InlineData("__vt__")]
    [InlineData("__vt__99TFooActor")]
    public void Non_vtable_symbols_are_rejected(string symbol)
    {
        Assert.False(CodeWarriorDemangler.TryGetVtableClass(symbol, out _));
    }

    [Fact]
    public void Functions_split_into_scope_name_and_arguments()
    {
        Assert.True(CodeWarriorDemangler.TryParseFunction("draw__Q23Gfx7TCanvasCFif", out var function));

        Assert.Equal("Gfx::TCanvas", function.Scope);
        Assert.Equal("draw", function.Name);
        Assert.True(function.IsConst);
        Assert.Equal(["int", "float"], function.Arguments);
        Assert.Equal("Gfx::TCanvas::draw", function.QualifiedName);
    }

    [Fact]
    public void Void_argument_list_is_empty()
    {
        Assert.True(CodeWarriorDemangler.TryParseFunction("update__9TFooActorFv", out var function));

        Assert.Empty(function.Arguments!);
        Assert.False(function.IsConst);
    }

    [Theory]
    [InlineData("__vt__9TFooActor")]
    [InlineData("sInstance__9TFooActor")]
    [InlineData("@32@update__9TFooActorFv")]
    [InlineData("plainName")]
    public void Non_functions_are_not_parsed(string symbol)
    {
        Assert.False(CodeWarriorDemangler.TryParseFunction(symbol, out _));
    }
}
