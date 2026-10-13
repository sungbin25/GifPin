# GifPin

화면 위에 투명 배경 GIF를 띄우는 Windows 프로그램. 게임이나 다른 프로그램 위에서도 항상 보이고, 클릭은 아래로 통과합니다.

## 설치

[Releases](../../releases/latest)에서 받습니다. (main에 올라가면 자동으로 새 릴리스가 만들어집니다)

- `GifPin-Setup-x.y.z.exe` — 설치 프로그램 (관리자 권한 필요 없음)
- `GifPin-portable-x.y.z.exe` — 설치 없이 바로 실행

처음 실행하면 영어로 나옵니다. **Settings → Language → 한국어**로 바꾸면 바로 적용됩니다.

## 사용법

| 동작 | 방법 |
| --- | --- |
| 사진 추가 | 메인 창에 GIF를 끌어다 놓기, 또는 "PC에서 추가" |
| 화면에 띄우기 | 카드 더블클릭, 또는 카드 위 "화면에 추가" |
| 사진 교체 | 아래 "화면에 표시 중"에서 GIF 선택 → 카드의 "교체" (위치·크기 유지) |
| 위치·크기 조정 | "편집 모드" 또는 `Ctrl+Alt+E` → 드래그 / 모서리 / 휠 → "완료" |
| 배경 지우기 | 카드 우클릭 → "배경 지우기…" |
| 스프라이트 / 프레임 폴더 | "스프라이트" → 시트 이미지(칸 수 자동 추정) 또는 PNG 프레임 폴더 → 라이브러리에 WebP로 추가 |
| 팩 공유 | "팩" → 화면 배치·즐겨찾기·전체를 `.zip`으로 내보내기 / 가져오기 (`.zip`을 끌어다 놓아도 됨) |
| 마우스로 옮기기 | 속성에서 "클릭 통과"를 끄면 화면의 GIF를 바로 끌어서 옮길 수 있음 |
| 움직이게 하기 | 화면의 GIF 선택 → 속성의 "움직임": 걷기 / 튕기기 / 떨어지기(점프 높이·간격·각도) / 미끄러짐 |
| 움직이는 GIF 잡기 | 0.2초 이상 누르면 멈추고 잡힘, 놓으면 0.5초 뒤 다시 움직임 (설정 → 마우스에서 조정) |
| 던지기 | "미끄러짐"에서 GIF를 끌다가 놓으면 마우스 속도만큼 화면에 튕기며 미끄러지다 멈춤 |
| 모니터 옮기기 | 속성의 "모니터"에서 선택. 모니터를 빼면 그 위의 GIF는 주 모니터로 이동 |
| GIF를 잃어버렸을 때 | 설정 → 문제 해결, 또는 트레이 → 문제 해결: 가운데로 모으기 / 모두 보이기 / 클릭 통과 끄기 |
| 오류 보고 | 설정 → 진단 → "진단 정보 복사" (로그: `%AppData%\GifPin\logs\gifpin.log`) |
| 모두 숨기기 | `Ctrl+Alt+H` |
| 종료 | 트레이 아이콘 메뉴 → "종료", 또는 `Ctrl+Alt+Q` (창의 X는 트레이로 숨김) |

게임은 **테두리 없는 창 모드**로 실행해야 GIF가 위에 보입니다. 독점 전체화면에서는 표시되지 않습니다.

## 구조

```
src/GifPin/
  Core/   OverlayEngine (전용 스레드), OverlayWindow (레이어드 윈도우 + 편집 핸들),
          GifAnimation, ApngDecoder, BackgroundRemover, WebpWriter, SpriteBuilder,
          AssetPack, Motion, Monitors, Library, AppState/Store, Log, Loc/Strings (영어·한국어)
  UI/     MainWindow, EditToolbar, BgRemoveWindow, SpriteImportWindow, TrayIcon, AppController
tests/    스모크 테스트용 샘플 (GIF, APNG, 스프라이트 시트, 프레임 폴더)
installer/GifPin.iss   Inno Setup 스크립트
.github/workflows/     Windows 빌드 + 스모크 테스트 + 설치 파일
```

- .NET 8 / WPF, SkiaSharp
- 데이터: `%AppData%\GifPin\` (state.json, Library\, Thumbs\, logs\)
- 지원 형식: GIF, 애니메이션 WebP, APNG, PNG, JPG, BMP
- 새 문구는 `Core/Strings.cs`에 영어·한국어를 함께 추가 (CI가 빠진 번역을 검사)

## 직접 빌드

```powershell
dotnet publish src/GifPin/GifPin.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```
