@echo off
setlocal
set "APPDIR=D:\Projetos\LinkFlow\bin\LinkFlow"
set "NEWDIR=D:\Projetos\LinkFlow\bin\LinkFlow_v2"
set "SVC=LinkFlowService"

rem --- Precisa de administrador para parar/iniciar o servico ---
net session >nul 2>&1
if %errorlevel% neq 0 (
    powershell -NoProfile -Command "Start-Process '%~f0' -Verb RunAs"
    exit /b
)

echo ===================================================
echo   LinkFlow - Atualizando e Iniciando Nova Versao
echo ===================================================
echo.
echo 1. Compilando e publicando a nova versao em %NEWDIR%...
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\publish.ps1" -Output "%NEWDIR%"
if %errorlevel% neq 0 (
    echo Falha na compilacao. Nada foi alterado.
    pause
    exit /b 1
)

echo 2. Encerrando a interface de forma controlada...
if exist "%APPDIR%\LinkFlow.App.exe" "%APPDIR%\LinkFlow.App.exe" --exit
timeout /t 1 /nobreak >nul
taskkill /IM LinkFlow.App.exe >nul 2>nul
timeout /t 2 /nobreak >nul
taskkill /F /IM LinkFlow.App.exe >nul 2>nul

set "HASSVC=0"
sc query %SVC% >nul 2>&1 && set "HASSVC=1"
if "%HASSVC%"=="1" (
    echo 3. Parando o servico %SVC% ^(o hub volta ao modo hardware^)...
    sc stop %SVC% >nul 2>&1
    timeout /t 3 /nobreak >nul
)

echo 4. Atualizando binarios em %APPDIR%...
xcopy /Y /E /I "%NEWDIR%\*" "%APPDIR%\" >nul

if "%HASSVC%"=="1" (
    echo 5. Iniciando o servico %SVC%...
    sc config %SVC% start= auto >nul 2>&1
    sc start %SVC% >nul 2>&1
    timeout /t 2 /nobreak >nul
)

echo 6. Abrindo o LinkFlow...
if "%HASSVC%"=="1" (
    rem Com servico: interface como usuario comum (explorer nao herda o administrador deste script)
    explorer.exe "%APPDIR%\LinkFlow.App.exe"
) else (
    rem Sem servico: modo local como administrador (temperatura da CPU disponivel)
    start "" "%APPDIR%\LinkFlow.App.exe"
)
echo.
echo Concluido! Dica: em Configuracoes ^> Servico do Windows voce pode instalar o servico
echo para o controle continuar rodando mesmo sem a janela aberta.
timeout /t 5 >nul
