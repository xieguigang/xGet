@echo off

set http="bin\Fluteway.exe"
set gb=1073741824

call %http% /run --app ./bin/Nuget.dll --listen 80 --wwwroot ./wwwroot --data ./data/ --max-post-size %gb% --base-url http://localhost/
