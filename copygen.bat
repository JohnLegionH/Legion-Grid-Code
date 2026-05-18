@echo off
set SRC=D:\legion-grid-source\OpenSim\Addons\Phlox\grammar\generated
set COMP=D:\legion-grid-source\OpenSim\Addons\Phlox\InWorldz.Phlox\Compiler
set BYTE=D:\legion-grid-source\OpenSim\Addons\Phlox\InWorldz.Phlox\ByteCompiler

copy /y "%SRC%\LSLLexer.cs" "%COMP%\LSLLexer.cs"
copy /y "%SRC%\LSLParser.cs" "%COMP%\LSLParser.cs"
copy /y "%SRC%\LSLVisitor.cs" "%COMP%\LSLVisitor.cs"
copy /y "%SRC%\LSLBaseVisitor.cs" "%COMP%\LSLBaseVisitor.cs"
copy /y "%SRC%\AssemblerLexer.cs" "%BYTE%\AssemblerLexer.cs"
copy /y "%SRC%\AssemblerParser.cs" "%BYTE%\AssemblerParser.cs"
copy /y "%SRC%\AssemblerVisitor.cs" "%BYTE%\AssemblerVisitor.cs"
copy /y "%SRC%\AssemblerBaseVisitor.cs" "%BYTE%\AssemblerBaseVisitor.cs"
echo Done.