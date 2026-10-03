# BullsEye — 설계 계획서

> 어느 Windows 앱에서든 코드를 드래그하고 단축키를 누르면, 작은 팝업에서 AI에게 지시하고 결과를 클립보드로 받는 USB 포터블 도구.
> AI 플러그인을 쓸 수 없는 구형 IDE(MATLAB 등)가 주 대상.

---

## 1. 확정된 결정

| 영역 | 결정 |
|---|---|
| 결과 전달 | **클립보드**에만 넣음. 사용자가 IDE에서 `Ctrl+V` (선택 영역이 그대로 남아 있어 교체됨) |
| 선택 텍스트 획득 | 단축키 → 수식키 해제 대기 → `Ctrl+C` 시뮬레이션 → 클립보드 읽기. 클립보드 시퀀스 번호가 안 바뀌면 "선택 없음" → **생성 모드** |
| 원래 클립보드 | 복원하지 않음 (결과가 덮어쓰므로 무의미) |
| 기술 스택 | C# / **.NET 10 (LTS)** / WinForms, self-contained 단일 exe (`win-x64`) |
| 배포 | **USB 포터블**. 설치·레지스트리·자동 실행 없음. 모든 경로는 exe 기준 상대 경로 |
| 사용자 | 나 혼자. 내 PC + 수업 공용 PC (학교망, 외부 인터넷 가능) |
| AI Provider | **OpenRouter** (OpenAI 호환 `/api/v1/chat/completions`, SSE 스트리밍). `IChatProvider` 인터페이스로 추상화 |
| 모델 | 설정에서 즐겨찾기 2~4개 선택(`/api/v1/models`로 목록 조회). 기본 = 빠른 모델(Haiku 계열), 2순위 = 정확한 모델(Sonnet 계열). 팝업에서 `[모델 ▾]` / `Ctrl+Tab` 순환 |
| 데이터 정책 | 요청에 `provider.data_collection: "deny"` 기본 적용, 설정에서 끌 수 있음 |
| 추론(reasoning) | **기본 켬** (`reasoning: {enabled: true}`, 정확도 우선). 설정에서 끌 수 있음. `max_tokens`는 보내지 않음 (추론이 코드를 잘라먹지 않게) |
| 컨텍스트 | 선택 텍스트 + 지시문 + **언어 힌트**(활성 창 프로세스명/창 제목/확장자 → 언어). 매핑은 설정, 팝업에서 수동 변경 가능 |
| 응답 형식 | **코드만**. 시스템 프롬프트로 강제 + 코드 펜스(```` ``` ````) 후처리 제거 |
| 들여쓰기 | 선택 영역 첫 줄 들여쓰기를 결과 전체에 적용 |
| 팝업 동작 | 결과 **스트리밍 표시** → 완료 시 클립보드 + 팝업 유지. Esc / 외부 클릭으로 닫힘 → 원래 창으로 포커스 복귀 |
| 재생성 | **대화 이어가기**(원본+이전 지시+이전 결과+새 지시). 빈 지시 + Enter = 같은 지시 재생성. 요청 실패 후 새 지시는 실패한 지시를 **교체**. 팝업 닫으면 대화 초기화 |
| 팝업 위치 | 캐럿 아래(`GetGUIThreadInfo`) → 실패 시 마우스 위치. 모니터 경계 보정 |
| 단축키 | 기본 `Ctrl+Alt+K` (개발 PC에서 `Ctrl+Alt+Space`가 다른 프로그램에 선점되어 변경), 설정에서 변경. 등록 실패 시 트레이 알림 |
| 히스토리 | 최근 지시문 N개만 USB(암호화 파일)에 저장, 팝업에서 `↑`로 불러오기. 코드 내용은 저장 안 함 |
| 실행 흐름 | 매 실행 시 설정 창 → 닫으면 트레이 상주. 트레이 메뉴: `설정` / `일시정지` / `종료` |
| 중복 실행 | 차단. 두 번째 실행은 기존 인스턴스의 설정 창을 띄움 |

### 보안 / 흔적

| 항목 | 결정 |
|---|---|
| 키 저장 | USB의 `BullsEye.dat` — 설정 전체(키, 히스토리 포함)를 **PIN 암호화** |
| PIN | **영문+숫자 8자 이상**. KDF = Argon2id(약 0.5s/시도), 암호화 = AES-256-GCM |
| PIN 분실 | 설정 창 `초기화` → 파일 삭제 후 재입력 (복구 수단 없음) |
| 키 표시 | 마스킹(`sk-or-…a1b2`), 다시 보기 기능 없음 |
| USB 제거 | 감지 즉시 종료 (메모리 키 소멸) |
| 유휴 종료 | 기본 60분 무사용 시 종료, 5분 전 트레이 알림, 설정에서 변경 |
| 클립보드 기록 | 우리가 쓰는 클립보드에 기록/클라우드 제외 플래그. 종료 시 우리가 넣은 내용이면 클립보드 삭제 |
| 크래시 | 전역 예외 처리 → 깔끔하게 종료 |
| 운영 규칙 | OpenRouter에서 **이 앱 전용 키 + 키별 크레딧 한도** 설정. USB 분실 시 즉시 키 폐기 |

### 에러 처리

| 상황 | 동작 |
|---|---|
| 네트워크 실패 / 타임아웃 | 결과 영역에 빨간 한 줄 + "Enter로 재시도". 연결 10s, 첫 응답(추론 또는 코드) 30s |
| 401 | "키를 확인하세요" + 설정 열기 링크 |
| 402 | "OpenRouter 크레딧 부족" |
| 429 / 5xx 과부하 | 1회 자동 재시도 후 메시지 |
| 스트림 중 에러 이벤트 | 받은 부분은 유지, 에러 표시 |
| 요청 중 Esc | 요청 취소(`CancellationToken`) |

---

## 2. 아키텍처

```
BullsEye.exe (WinForms, Win32)                  BullsEye.Core (순수 로직, TDD)
──────────────────────────────                  ──────────────────────────────
TrayController ─────────┐                       Settings / SettingsSerializer
SettingsForm (unlock) ──┼──► AppController ───► SecureStore (Argon2id + AES-GCM)
PromptPopup ────────────┘        │              PromptHistory (ring buffer)
                                 │              LanguageDetector
HotkeyManager (RegisterHotKey) ──┤              ConversationBuilder (system prompt, turns)
SelectionCapture (SendInput)  ───┤              ResponsePostProcessor (fence strip, indent)
ClipboardService (history 제외) ─┤              IChatProvider
CaretLocator (GetGUIThreadInfo) ─┤                ├─ OpenRouterProvider (HttpClient + SseParser)
ForegroundWindowInfo ────────────┤                └─ MockProvider (개발용, BULLSEYE_MOCK=1)
UsbWatcher (WM_DEVICECHANGE) ────┤              ProviderErrorMapper → BullsEyeException 계층
SingleInstance (Mutex + pipe) ───┘              ModelCatalog (/models)
                                                IdleTracker (IClock 주입)
```

- Win32 기능은 전부 얇은 인터페이스(`ISelectionCapture`, `IClipboardService` 등) 뒤에 둠 → `AppController`는 가짜 구현으로 테스트 가능.
- 예외는 `BullsEyeException` 계층(`AuthException`, `CreditException`, `RateLimitException`, `NetworkException`, `TimeoutException`)으로 통일, UI는 이것만 보고 메시지 결정.
- `HttpClient`는 시스템 프록시 설정을 따름.

### 단축키 한 번의 흐름

```
Ctrl+Alt+K
  → ForegroundWindowInfo: hwnd, 프로세스명, 창 제목 저장
  → 수식키(Ctrl/Alt/Shift/Win) 해제 대기 (최대 1s)
  → seq0 = GetClipboardSequenceNumber(); SendInput(Ctrl+C); seq 변화 대기 (최대 300ms)
      ├─ 변화 있음 → 선택 텍스트 획득 (편집 모드)
      └─ 변화 없음 → 생성 모드
  → LanguageDetector로 언어 힌트
  → CaretLocator로 위치 결정 → PromptPopup 표시
  → 사용자 지시 입력 + Enter
  → ConversationBuilder → OpenRouterProvider.StreamAsync → 팝업에 실시간 표시
  → 완료: ResponsePostProcessor (펜스 제거, 들여쓰기) → ClipboardService.Set(기록 제외)
  → "✓ 복사됨" → (재지시 or Esc) → 원래 hwnd로 포커스 복귀
```

---

## 3. 프로젝트 구조

```
Project_BullsEye/
├─ BullsEye.sln
├─ mise.toml                    # build / test / publish 태스크
├─ docs/
│  ├─ PLAN.md
│  └─ MANUAL_TEST.md            # 수동 체크리스트
├─ src/
│  ├─ BullsEye/                 # WinForms 앱 (net10.0-windows)
│  └─ BullsEye.Core/            # 순수 로직 (net10.0)
└─ tests/
   └─ BullsEye.Core.Tests/      # xUnit
```

USB 레이아웃: `BullsEye.exe` + `BullsEye.dat`(첫 실행 후 생성).

---

## 4. 구현 마일스톤

| # | 내용 | 완료 기준 |
|---|---|---|
| M0 | .NET 10 SDK 설치, 솔루션/프로젝트 생성, `mise.toml`, `.gitignore` | `mise run build`, `mise run test` 통과 |
| M1 | Core TDD: `ResponsePostProcessor`, `LanguageDetector`, `SseParser`, `ProviderErrorMapper`, `PromptHistory`, `ConversationBuilder` | 단위 테스트 녹색 |
| M2 | `SecureStore` (Argon2id + AES-GCM, 왕복 / 잘못된 PIN / 변조 감지 테스트), `Settings` 직렬화 | 단위 테스트 녹색 |
| M3 | `OpenRouterProvider` + `ModelCatalog` + `MockProvider` | 실제 키로 스트리밍 스모크 테스트 |
| M4 | Win32 레이어: 단축키, 선택 캡처, 클립보드(기록 제외), 캐럿 위치, 전경 창 정보 | 메모장에서 수동 확인 |
| M5 | UI: 설정 창(첫 실행 / 잠금 해제), 팝업, 트레이 | 전체 흐름 동작 |
| M6 | 수명주기: USB 제거 종료, 유휴 종료 + 알림, 단일 인스턴스, 전역 예외, 종료 시 클립보드 정리 | 체크리스트 해당 항목 통과 |
| M7 | 배포: `dotnet publish` self-contained 단일 파일, `IncludeNativeLibrariesForSelfExtract=false`, `%TEMP%` 추출 없음 확인 | USB에서 공용 PC 실행 성공 |

---

## 5. 수동 테스트 체크리스트 (요약 — 상세는 `MANUAL_TEST.md`)

- [ ] 메모장 / VS Code / **MATLAB 에디터**에서 선택 → 결과 → `Ctrl+V` 교체
- [ ] 선택 없이 단축키 → 생성 모드
- [ ] 한글 지시문 입력 (IME)
- [ ] MATLAB에서 캐럿 위치 실패 → 마우스 위치 폴백
- [ ] Win+V 클립보드 기록에 결과가 남지 않음
- [ ] 실행 중 USB 제거 → 즉시 종료
- [ ] 유휴 시간 경과 → 알림 → 종료
- [ ] 잘못된 PIN / 초기화
- [ ] 네트워크 끊김, 잘못된 키(401), 크레딧 0(402)
- [ ] 공용 PC (학교망)에서 OpenRouter 접속
- [ ] 실행 후 `%TEMP%`에 파일이 생기지 않음

---

## 6. 알려진 리스크

| 리스크 | 대응 |
|---|---|
| MATLAB 에디터(Java)가 `SendInput` Ctrl+C에 반응이 늦음 | 시퀀스 번호 대기 시간을 설정값으로 노출 |
| 대상 앱이 관리자 권한이면 UIPI로 키 입력 차단 | 감지 시 "관리자 권한 앱에서는 동작하지 않음" 안내 (공용 PC에선 드물 것) |
| 학교 백신이 키 입력 시뮬레이션 / 서명 없는 exe를 차단 | M7에서 공용 PC 실기 테스트로 조기 확인 |
| 공용 PC가 느리면 Argon2id 잠금 해제가 느림 | 파라미터는 파일 헤더에 저장 → 나중에 조정 가능 |
| 학교망이 `openrouter.ai` 차단 | M3 직후 공용 PC에서 접속만 먼저 확인 |
