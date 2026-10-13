# GifPin

화면 위에 투명 배경 GIF를 띄우는 Windows 프로그램. 게임이나 다른 프로그램 위에서도 항상 보이고, 클릭은 아래로 통과합니다.

## 설치

[Actions](../../actions) 탭 → 최신 **Build** 실행 → 아래 **Artifacts**의 `GifPin-x.y.z`를 받아 압축을 풉니다.

- `GifPin-Setup-x.y.z.exe` — 설치 프로그램 (관리자 권한 필요 없음)
- `GifPin-portable-x.y.z.exe` — 설치 없이 바로 실행

`v`로 시작하는 태그(예: `v0.2.0`)를 올리면 [Releases](../../releases)에도 자동으로 올라갑니다.

## 사용법

| 동작 | 방법 |
| --- | --- |
| 사진 추가 | 메인 창에 GIF를 끌어다 놓기, 또는 "PC에서 추가" |
| 화면에 띄우기 | 카드 더블클릭, 또는 카드 위 "화면에 추가" |
| 사진 교체 | 아래 "화면에 표시 중"에서 GIF 선택 → 카드의 "교체" (위치·크기 유지) |
| 위치·크기 조정 | "편집 모드" 또는 `Ctrl+Alt+E` → 드래그 / 모서리 / 휠 → "완료" |
| 배경 지우기 | 카드 우클릭 → "배경 지우기…" |
| 모두 숨기기 | `Ctrl+Alt+H` |
| 종료 | 트레이 아이콘 메뉴 → "종료", 또는 `Ctrl+Alt+Q` (창의 X는 트레이로 숨김) |

게임은 **테두리 없는 창 모드**로 실행해야 GIF가 위에 보입니다. 독점 전체화면에서는 표시되지 않습니다.

## 구조

```
src/GifPin/
  Core/   OverlayEngine (전용 스레드), OverlayWindow (레이어드 윈도우 + 편집 핸들),
          GifAnimation, BackgroundRemover, WebpWriter, Library, AppState/Store
  UI/     MainWindow, EditToolbar, BgRemoveWindow, TrayIcon, AppController
installer/GifPin.iss   Inno Setup 스크립트
.github/workflows/     Windows 빌드 + 스모크 테스트 + 설치 파일
```

- .NET 8 / WPF, SkiaSharp
- 데이터: `%AppData%\GifPin\` (state.json, Library\, Thumbs\)

## 직접 빌드

```powershell
dotnet publish src/GifPin/GifPin.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```
