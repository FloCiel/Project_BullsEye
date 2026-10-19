# BullsEye 에이전트 하네스 개선 리서치 (2026-10-19)

범위: GitHub 저장소, 공식 문서, 엔지니어링 블로그, 논문. 유료 LLM API는 호출하지 않았다. 코드는 수정하지 않았다.

신뢰도 표기:
- [P] 직접 확인한 1차 자료(공식 문서/저장소/논문)
- [S] 2차 자료(블로그, 이슈 요약, 검색 결과 요약)
- [U] 직접 확인하지 못했고 일반 지식에 의존. 구현 전에 검증 필요

수집 한계(coverage): 직접 fetch는 대략 18건 중 12건 성공했다. opencode `edit.ts`, Codex `apply_patch_tool_instructions.md`, SWE-agent 논문 본문은 raw 접근이 404였거나 초록만 받았다. 그래서 해당 내용은 검색 결과 요약[S]에 의존한다. 위 항목과 pi-mono의 tools 소스는 직접 확인하지 못했다.

---

## 우선순위 권고 (영향 / 구현비용 순)

### 1. `rename_file` (move) + `delete_file` 도구 추가 — 비용 S, 영향 최상
- 변경: 샌드박스 안에서만 동작하는 `move_file(from, to)`와 `delete_file(path)`를 추가한다. 둘 다 기존 approver를 통과시키고, 실행 전에 exe 옆 백업 폴더로 복사한다. `to`가 이미 있으면 거부한다. delete는 "백업 후 이동" 방식으로 구현하면 사실상 휴지통이 된다.
- 이유: 최근 실패(MATLAB 함수 rename 시 새 파일과 wrapper를 만든 것)의 근본 원인이 도구 부재다. 프롬프트로 "사용자에게 파일명 변경을 안내하라"고 하면 window-less 모드에서는 사용자가 받을 방법이 클립보드 주석뿐이다. 도구가 있으면 모델이 한 번에 끝낼 수 있다.
- 근거:
  - Codex `apply_patch`는 `*** Delete File`과 `*** Update File` + `*** Move to`를 1급 연산으로 둔다 [S: Codex 공식 instructions 파일을 검색 요약으로 확인].
  - Anthropic은 겹치지 않는 도구를 두되 한 작업은 한 도구로 끝나게 하라고 권한다 [P].
  - Anthropic 텍스트 에디터 문서는 편집 전 백업을 권장한다 [P].
- 구현 메모: 도구 설명에 "MATLAB은 파일명 = 함수명이어야 하므로 함수 이름을 바꿀 때 move_file로 파일도 같이 옮기고, 호출하는 다른 파일도 edit_file로 고친다"를 명시한다. 시스템 프롬프트의 "사용자에게 rename을 안내" 문구는 삭제한다.
- 위험:
  - 열려 있는 에디터 탭이 가리키는 파일이 사라진다 (아래 7번의 에디터 버퍼 문제와 연결).
  - 삭제 범위를 work 폴더 안으로 엄격히 제한하고 디렉터리 삭제는 금지한다.
  - Codex에는 Move to가 원본과 같은 경로일 때 파일이 삭제되는 버그가 보고되어 있다 [S: openai/codex #34210]. from == to 와 대소문자만 다른 경로를 명시적으로 처리해야 한다.

### 2. edit_file 실패 시 "자기수정 가능한" 오류 메시지 + 보수적 fuzzy fallback — 비용 S~M
- 변경:
  1. 0건 매치일 때 가장 비슷한 구간(줄 단위 유사도 상위 1~2개)을 줄 번호와 함께 돌려준다.
  2. 다중 매치일 때 "N곳 일치, 각 줄 번호는 …, 더 많은 주변 문맥을 포함하라"를 돌려준다.
  3. 정확 일치 실패 시에만 아래 순서로 제한적 fallback을 적용하고, 결과가 정확히 1곳일 때만 적용한다.
     - 줄 끝 공백 제거
     - 줄별 trim 비교(들여쓰기 무시)
     - CRLF/LF 정규화
     - 적용했다면 응답에 "공백 차이를 보정해 적용함"이라고 알린다.
- 이유: 학생 PC의 MATLAB 파일은 CRLF, 탭/스페이스 혼용, 끝 공백이 흔하다. 모델은 이를 정확히 재현하지 못한다.
- 근거:
  - Aider 벤치마크: 유연한 패치 적용을 끄면 편집 오류가 9배 늘었다 [P: aider.chat/docs/unified-diffs.html].
  - opencode는 Simple → LineTrimmed → BlockAnchor → WhitespaceNormalized → IndentationFlexible → … 의 9단계 cascade를 쓴다 [S].
  - Anthropic 텍스트 에디터 문서의 오류 예시는 "Found 3 matches … provide more context", "No match found …"이다 [P].
  - "Writing tools for agents": 오류는 불투명한 코드가 아니라 고치는 방법을 알려야 한다 [P].
- 위험:
  - opencode의 fuzzy 단계는 모호성 검사가 SimpleReplacer에만 적용되어 의도치 않은 위치에 적용될 수 있었다 [S: opencode issue #1261, #41872]. BlockAnchor(Levenshtein) 같은 공격적 단계는 도입하지 말고, trim 계열 2~3단계만 둔다.
  - 모든 fuzzy 후보를 수집해 중복 제거 후 정확히 1개일 때만 적용한다.
  - 어차피 approver가 diff를 보여주므로 위험은 줄어든다. 단, window-less 모드는 auto-approve라서 더 보수적이어야 한다.

### 3. 다중 편집(`edits[]`) 지원과 read_file 줄 번호 옵션 — 비용 S
- 변경:
  - `edit_file`에 `edits: [{old_text, new_text}, ...]` 형태를 허용한다. 모두 원본 기준으로 검증한 뒤 원자적으로 적용하고, 하나라도 실패하면 전부 롤백한다. 한 번의 diff 승인 창으로 묶는다.
  - `read_file`에 줄 번호를 붙이되(`  12\t...`), `edit_file`의 old_text에는 줄 번호를 넣지 말라고 명시한다. 또는 `show_line_numbers` 옵션으로 둔다.
- 이유:
  - 함수 하나를 rename하면 한 파일 안에서 여러 곳을 고쳐야 하고, 호출이 여러 번이면 승인 창이 여러 번 뜬다.
  - 줄 번호는 오류 메시지의 위치 안내, 리뷰 시 "N번째 줄 버그" 지적, offset/limit 읽기에 필요하다.
- 근거:
  - Anthropic 텍스트 에디터 문서: 줄 번호는 필수는 아니지만 `view_range`와 `insert_line` 같은 위치 지정에 필수적이다 [P].
  - Aider는 diff hunk에서 줄 번호를 빼는 것이 핵심이었다고 한다. 모델이 줄 번호 계산에 약하기 때문이다 [P]. 그래서 번호는 읽기 전용 참조용으로만 쓰고 편집 인자로는 쓰지 않는다.
  - "Building effective agents": 줄 수 세기나 이스케이프 같은 오버헤드가 큰 형식은 피하라 [P].
- 위험: 모델이 줄 번호 접두어를 old_text에 복사한다. 도구 설명에서 금지하고, 실패 시 "줄 번호 접두어가 포함된 것 같다"는 힌트를 준다.

### 4. grep(search) 도구와 list_files 개선 — 비용 S
- 변경: `search_files(pattern, path, glob, max_results)`를 추가한다. 일반 텍스트/정규식 검색 결과를 `파일:줄:내용` 형식으로 최대 N건 돌려주고, 잘렸으면 "더 좁혀서 검색하라"고 안내한다. `list_files`도 잘림 안내를 추가한다.
- 이유: 함수 rename은 호출부를 찾아야 완결된다. 지금은 모든 파일을 read해야 한다. 최대 300개 제한 때문에 큰 폴더에서는 놓칠 수 있다.
- 근거:
  - pi는 기본 도구가 read/bash/edit/write 4개이고 grep/find/ls는 읽기 전용 옵션으로 제공하되 기본은 꺼 둔다 [P: Zechner 블로그]. pi는 bash로 grep을 쓸 수 있어서 가능한 선택이다. BullsEye는 bash가 없으므로 grep을 기본 켜는 것이 맞다.
  - SWE-agent ACI 논문은 파일 탐색/검색 도구 설계가 성능에 큰 영향을 준다고 보고한다 [P: 초록 확인. 세부 설계(검색 결과 요약, 린트 후 편집 등)는 본문을 직접 확인하지 못해 [U]].
  - Anthropic은 응답 크기를 제한하고(Claude Code는 도구 응답 기본 25,000 토큰), 잘렸을 때 다음 행동을 안내하라고 한다 [P].
- 위험: 결과 크기 폭주. 파일당·전체 건수 상한을 둔다. 바이너리 파일(.mat, .p, .fig)은 건너뛴다.

### 5. 시스템 프롬프트 개편: 범위 제한과 무질문 자율 모드 — 비용 S
- 변경 (프롬프트 문구 수준):
  - "요청된 변경만 한다. 호환용 wrapper, 별칭, 새 헬퍼 파일은 만들지 않는다. 필요해 보여도 요약에 제안으로만 적는다."를 최상단 규칙으로 올린다. 현재는 여러 규칙에 흩어져 있다.
  - 무질문 모드(window-less)에서는 "질문하지 말고 가장 합리적인 해석으로 진행한 뒤, 가정한 내용을 요약 첫 줄에 적는다."를 추가한다.
  - 완료 전 자기 점검을 넣는다: "변경한 파일 목록과 각 파일의 이유를 확인하고, 요청과 무관한 파일이 있으면 되돌린다(덮어쓰기 대신 원복 불가하므로 애초에 만들지 않는다)."
  - 요약 형식을 고정한다: 변경 파일 / 가정 / 사용자가 직접 해야 할 일(없으면 "없음"). MATLAB은 한 줄 `%` 주석 형태로 붙여넣기 쉽게 한다.
  - rename 규칙은 1번 도구를 만든 뒤 "move_file을 쓴다"로 교체한다.
- 근거:
  - Codex 프롬프트는 "내가 만들지 않은 변경은 되돌리지 말 것", "예상 밖의 변경이 보이면 멈추고 묻기", 간결한 최종 메시지와 파일 경로·줄 번호 참조를 규정한다 [P: gpt_5_codex_prompt.md 확인]. 단 명시적인 "질문 금지" 절은 없다. 무질문 모드 문구는 BullsEye가 직접 설계해야 한다.
  - pi는 시스템 프롬프트 + 도구 정의가 1,000토큰 미만이며 프론티어 모델은 코딩 에이전트 행동에 이미 RL되어 있다고 주장한다 [P: Zechner 블로그]. 긴 규칙 나열은 역효과일 수 있다.
  - Aider: "눈이 안 보이는 사용자", 팁 약속 같은 감정적 프롬프트는 점수를 낮췄다 [P]. 강조 남발도 피한다.
  - "Building effective agents": 도구 설명과 프롬프트에 예시·경계·엣지케이스를 넣고 실제 입력으로 반복 테스트하라 [P].
- 위험: 프롬프트만으로는 비결정적이다. 1번(도구)과 8번(사후 검사) 같은 구조적 장치와 병행해야 한다. 모델별 회귀 확인용으로 `BULLSEYE_MOCK=1` 시나리오 테스트를 만들어 둔다(유료 호출 없이 프롬프트 효과는 검증되지 않는다는 한계가 있다).

### 6. 편집 전 체크포인트(세션 단위 undo)와 "이번 실행 변경 목록" — 비용 M
- 변경: 에이전트 실행 1회(turn)마다 `run-YYYYMMDD-HHMMSS/` 백업 폴더를 만들고, 건드린 파일의 최초 원본만 저장한다(파일당 1회). 완료 시 manifest(변경/생성/이동/삭제 목록)를 남긴다. 트레이 메뉴에 "마지막 실행 되돌리기"를 추가한다. 새로 만든 파일은 되돌리기 시 삭제(휴지통행)한다.
- 이유: 지금의 "각 쓰기마다 exe 옆 백업"은 같은 파일을 여러 번 고치면 중간 상태가 섞이고, 한 번의 지시로 바뀐 것을 한 번에 되돌릴 수 없다. window-less의 auto-approve에서 특히 필요하다.
- 근거:
  - Claude Code는 매 턴 시작 시 체크포인트를 만들고 코드/대화 복원을 분리해 제공한다. 단, bash로 한 변경은 추적하지 못하고 외부 변경도 추적하지 못하며 git을 대체하지 않는다고 명시한다 [P: code.claude.com/docs/en/checkpointing].
  - Cline은 도구 사용마다 shadow git 저장소에 커밋하며 Restore Files / Task Only / Both를 제공한다 [P: docs.cline.bot/features/checkpoints].
  - Aider는 변경마다 자동 커밋하고 `/undo`를 제공하며, 사용자의 미커밋 변경은 먼저 따로 커밋해 분리한다 [P: aider.chat/docs/git.html].
- BullsEye 적용: USB 환경에서 git이 없을 가능성이 크므로 shadow git 대신 단순 폴더 복사 + manifest가 적합하다. 복원 시 현재 디스크 내용이 에이전트가 마지막에 쓴 내용과 다르면(사용자가 이후 수정) 덮어쓰기 전에 경고한다.
- 위험: USB 용량·쓰기 속도, 폴더 누적(최근 N회만 보관), 학생이 백업 폴더를 제출물에 섞는 문제. 백업 위치를 work 폴더 밖(exe 옆)으로 유지한다.

### 7. 오래된 파일(stale) 검사와 에디터 미저장 버퍼 대응 — 비용 M
- 변경:
  - read_file 시 `(mtime, size, 해시)`를 기억한다. edit/write/move/delete 직전에 디스크 상태가 읽은 시점과 다르면 "읽은 뒤 파일이 바뀌었다. 다시 읽어라"를 반환한다. 읽지 않은 기존 파일에 대한 write_file 덮어쓰기는 거부한다("read before write").
  - window-less 모드: 핫키로 선택 텍스트를 가져오는 시점에 에디터가 파일을 저장했는지 알 수 없으므로, 실행 시작 시 대상 파일 mtime을 기록하고 종료 시점에도 비교한다. 사용자가 실행 중 같은 파일을 수정했다면 쓰지 않고 결과를 클립보드 주석으로만 낸다.
  - MATLAB Editor의 미저장 버퍼 감지는 외부 앱에서 신뢰할 방법이 없다. 대안은 (a) 핫키 직전에 에디터로 Ctrl+S를 보내거나 (b) 클립보드 선택 텍스트와 디스크 파일 내용이 다르면(선택 영역이 파일에 포함되는지 확인) "저장되지 않은 변경이 있을 수 있음"을 상태 점에 표시하는 것이다. 이 방식은 [U]이며 실제 MATLAB 환경에서 검증이 필요하다.
- 근거:
  - Codex 프롬프트: 작업 중 작업 트리가 예기치 않게 변하면 중단하고 확인하라 [P].
  - Claude Code 체크포인트 문서: 외부(수동) 변경은 추적되지 않는다는 한계를 명시한다 [P]. 즉 이 검사는 하네스가 직접 해야 한다.
  - "file modified since read" 류의 오류 메시지를 Claude Code 등이 쓴다는 점은 일반적으로 알려져 있으나 이번 조사에서 1차 자료로 직접 확인하지 못했다 [U]. 개념은 낙관적 동시성 제어로 구현이 단순하다.
- 위험: 클라우드 동기화·바이러스 검사로 mtime이 바뀌어 거짓 양성이 날 수 있다. 해시 비교를 병행한다.

### 8. 선택적 정적 검사 도구 `check_syntax` (Octave/MATLAB) — 비용 M, 보안 검토 필요
- 변경: 실행(`run`)이 아니라 구문/정적 검사만 하는 도구를 옵션으로 추가한다. 쓰기 후 자동으로 검사 결과를 같은 응답에 붙이는 형태(편집 직후 린트)도 가능하다.
  - MATLAB: `checkcode(filename, '-struct', '-id')`가 line, column, message를 돌려준다 [P: MathWorks 문서]. R2022b부터는 `codeIssues`가 권장된다 [P]. 단 문서가 이 함수가 코드를 실행하지 않는다고 명시하지는 않는다. 정적 분석 도구라는 성격상 실행하지 않는 것이 일반적이지만 확인은 못 했다 [U].
  - Octave: 문서화된 `--check-syntax`/`--parse-only` 옵션은 없다 [P: docs.octave.org Command-Line-Options]. 대안은 `octave --no-gui --norc --no-window-system --eval "..."` 로 스크립트를 실행하지 않고 `parse`만 하는 방법인데, 안전한 parse-only 호출이 확인되지 않았다 [U]. `--eval`은 실행이므로 임의 코드 실행 위험이 있어, 학생 PC 안전 원칙과 충돌한다.
- 이유: "구문 오류인데 모르고 끝남"을 줄인다. SWE-agent는 편집 직후 린트로 문법 오류가 있는 편집을 거부하는 방식을 쓴다 [U: 논문 본문 미확인, 일반적으로 알려진 ACI 설계].
- 게이트 사례: MathWorks 공식 MATLAB MCP 서버는 `check_matlab_code`(읽기 전용 정적 분석)와 `evaluate_matlab_code`, `run_matlab_file`, `run_matlab_test_file`을 분리해 제공한다. 보안 안내는 "모든 tool call을 검토하고 중요한 작업은 사람이 확인하라" 수준이다 [P: github.com/matlab/matlab-mcp-core-server]. 즉 정적 검사만 따로 열고 실행은 별도로 승인받는 구조가 선례다.
- 권고: 실행 도구(`run`)는 만들지 않는다(아래 "비권장" 참조). 정적 검사는 학교 PC에 MATLAB/Octave가 있는지, USB에 portable Octave를 넣을 수 있는지 먼저 확인한 뒤 결정한다. 우선순위는 낮다.
- 위험: 설치 의존, 실행 시간, MATLAB 시작 지연(수십 초), 라이선스. Octave가 `.m` 파일을 열면서 함수 정의/스크립트 최상위 코드를 실행할 수 있는지 반드시 확인해야 한다.

### 9. 루프 견고성: 도구 호출 ID, 병렬 호출, 텍스트로만 답하는 모델, 컨텍스트 정리 — 비용 M
- 변경 항목:
  1. 도구 결과 정리(tool-result clearing): 오래된 `read_file` 결과는 "(이전에 읽음: path, N줄)"로 치환한다. 마지막 user 메시지 이후의 항목은 건드리지 않는다.
  2. 30스텝 한계 도달 시 조용히 끊지 말고 마지막 2~3스텝 전에 "남은 스텝이 적다. 지금까지의 결과로 마무리하라"는 시스템 메시지를 주입하고, 한계 도달 시 현재까지 한 일을 요약해 클립보드/창에 낸다.
  3. 도구 호출 없이 텍스트로 "수정하겠습니다/아래 코드를 쓰세요"만 답하고 코드 블록을 출력하는 모델에 대한 가드: 수정 요청인데 쓰기 도구 호출이 0회이고 응답에 코드 블록이 있으면 1회에 한해 "도구로 직접 적용하라"고 재촉한다. 리뷰 모드(변경 금지)는 제외한다.
  4. 병렬 도구 호출: 한 응답에 여러 호출이 오면 쓰기 도구는 순차 실행하고(승인 창 순서 보장), 각 호출 ID마다 반드시 결과를 반환한다(오류여도). 하나라도 빠지면 일부 벤더가 400을 낸다.
  5. 도구 호출 ID 정규화: 벤더가 요구하는 ID 형식이 다르다(예: 길이/문자 제한). 내부 ID를 따로 두고 요청 시 벤더별로 변환한다 [U: 일반 지식, 구현 전 OpenRouter 라우팅 대상 벤더별로 확인].
  6. 인자 JSON 파싱 실패(잘린 JSON, 이중 인코딩)를 오류 결과로 되돌려 재시도하게 한다.
- ChatGPT plan(Responses API): 함수 호출 사이의 reasoning item은 반드시 그대로 다시 보내야 한다. 연속 호출이면 마지막 user 메시지 이후의 reasoning / function_call / function_call_output 항목을 모두 보존하고, 컨텍스트를 줄일 때도 이 구간은 변경하지 않는다. `store: false`이거나 ZDR이면 reasoning item의 `encrypted_content`를 되돌려 보낸다 [P: OpenAI reasoning guide]. 어시스턴트 메시지를 직접 재생할 때는 원래의 `phase` 값을 보존한다 [P, 같은 문서]. 위 1번의 도구 결과 정리를 이 구간에는 적용하지 않도록 주의한다.
- 근거:
  - Anthropic은 컨텍스트 엔지니어링으로 compaction, tool result clearing(가장 가벼운 compaction), 필요할 때만 읽기(just-in-time retrieval), 노트 파일을 권한다 [P: anthropic.com/engineering/effective-context-engineering-for-ai-agents].
  - "Building effective agents": 최대 반복 횟수 같은 stopping condition을 두라 [P].
  - pi는 compaction을 아직 구현하지 않았고 수백 회 교환도 문제없다고 보고했다 [P: Zechner 블로그]. 즉 30스텝 이내의 학생용 작업에서는 요약형 compaction까지는 불필요하고 1번 수준이면 충분하다.
  - 벤더 간 핸드오프: reasoning 처리가 벤더마다 달라 best-effort이며, Anthropic thinking은 다른 벤더로 넘길 때 `<thinking>` 텍스트로 바뀌고, 일부 벤더의 서명된 blob은 같은 메시지와 함께 재생해야 한다 [P: Zechner 블로그]. 세션 중 모델을 바꾸는 기능이 있다면 reasoning 항목을 버리거나 텍스트로 변환하는 정책이 필요하다.
- 위험: 3번의 재촉 가드는 정상적인 "설명만 요청" 질문에서 과잉 개입할 수 있다. 의도 분류 없이 단순 규칙(코드 블록 + 쓰기 0회 + 모드가 수정)으로 시작하고 1회 제한을 둔다.

### 10. 리뷰/수정 모드 구분과 편집 후 자기검증 — 비용 S
- 변경: 사용자의 `% >> 지시`가 "리뷰/설명"인지 "수정"인지 모델이 판단하게 하지 말고 지시 접두어(`% >> review:` 등)나 설정으로 모드를 명시한다. 리뷰 모드에서는 쓰기 계열 도구를 아예 비활성화한다. 수정 모드의 마지막 단계로 방금 쓴 파일을 한 번 다시 읽어 확인하도록 프롬프트에 넣는다.
- 이유: "리뷰 시 변경은 요청 시에만"을 프롬프트에 의존하는 것보다 도구 제거가 확실하다. poka-yoke 원칙이다.
- 근거: "Building effective agents"의 poka-yoke(인자/도구를 바꿔 실수를 어렵게 만들기) [P]. Codex 프롬프트의 리뷰 모드(버그 우선, 심각도 순, 파일:줄 참조) [P].
- 위험: 지시 문법이 복잡해지면 학생이 어려워한다. 기본은 자동이고 접두어는 선택으로 둔다.

---

## 고려했으나 권장하지 않음

| 항목 | 이유 |
|---|---|
| 범용 shell/`run` 도구 | 공용 PC 안전 원칙과 정면 충돌. MathWorks 공식 MCP 서버도 `evaluate_matlab_code`/`run_matlab_file`은 사람 확인을 전제한다 [P]. 필요하면 8번의 읽기 전용 정적 검사까지만. |
| Codex식 `apply_patch` 전체 포맷 도입 | 다중 파일, rename, delete에 편리하나 모델이 포맷을 틀리는 사례와 파서 이슈가 보고된다 [S: openai/codex #2030, #2578, #34210]. 1~3번(개별 도구 + `edits[]`)이 더 단순하고 승인 UI와도 맞다. 벤더 불문(OpenRouter) 환경에서는 `old_text/new_text`가 가장 널리 학습된 형식이다 (Anthropic `str_replace`, pi `edit`과 동일). |
| Aider식 unified diff 형식 | 큰 파일에서 lazy coding을 줄이는 효과가 입증되었으나(GPT-4 Turbo 20% → 61%) [P], 이는 구형 모델 기준이고 BullsEye의 파일은 작다. `edit_file` + 필요 시 `write_file`로 충분하다. |
| 공격적 fuzzy 매칭(Levenshtein 블록 앵커 등) | opencode에서 모호성 검사 우회, 성능, 오탐 이슈가 반복되었다 [S]. 2번의 보수적 trim 계열만 채택. |
| git 자동 커밋 / shadow git | USB·공용 PC에 git이 없을 수 있고 학생 폴더를 오염시킨다. 6번의 폴더 복사 체크포인트로 대체. |
| 서브에이전트 / plan 모드 / todo 도구 | pi가 의도적으로 제외했고 todo 목록이 모델을 혼란시킬 수 있다고 보고했다 [P]. 30스텝 이내 소규모 작업에는 과하다. |
| MCP 서버 연동 | 컨텍스트 오버헤드, 설치 의존. 포터블 USB 앱의 목적과 맞지 않는다 [P: pi 블로그의 MCP 비판은 컨텍스트 비용 관점]. |
| 감정적/강압적 프롬프트 | Aider 실험에서 오히려 점수가 하락했다 [P]. |

## MATLAB/Octave 특이사항 요약
- MathWorks 공식 MATLAB MCP Core Server(`check_matlab_code`, `evaluate_matlab_code`, `run_matlab_file`, `run_matlab_test_file`, `detect_matlab_toolboxes`)가 존재한다 [P: GitHub]. BullsEye에 직접 넣기는 무겁지만, "정적 검사는 읽기 전용, 실행은 별도 게이트"라는 도구 분리 방식은 참고할 만하다.
- 이번 조사에서 MATLAB Copilot 자체의 도구 설계나 프롬프트 공개 자료는 찾지 못했다 (미조사/미확인).
- 파일명 = 함수명 제약, `.p`/`.mat`/`.fig` 바이너리 보호, CRLF 처리는 BullsEye 고유 이슈로 위 1, 2, 4번에 반영했다.

## 현재 코드와 대조 (리뷰 메모, 2026-10-19)

리서치는 코드를 보지 않고 작성됐다. 실제 코드와 비교하면 아래가 다르다.

- **1번 move_file**: 사용자 결정은 "함수 이름 변경은 기본적으로 파일 안에서만, 함수 파일을 만들라고 할 때만 새 파일"이다 (`bb70ab7`). 그래서 move_file은 rename 기본 동작이 아니라, 사용자가 파일 이름 변경이나 이동을 명시했을 때 쓰는 도구로 둔다. 창 없는 모드에서는 편집기에 열린 탭이 사라지는 문제도 있다.
- **6번 체크포인트**: `AgentBackup`은 이미 세션마다 폴더 하나를 만들고 파일마다 최초 원본만 저장한다. "중간 상태가 섞인다"는 서술은 사실이 아니다. 남은 일은 새로 만든 파일 목록(manifest)과 "마지막 실행 되돌리기" 메뉴다. 실행 로그(`AgentLog`, `98ca338`)가 manifest 역할 일부를 한다.
- **7번 미저장 버퍼**: 창 없는 모드는 이미 창 제목의 `*`로 미저장 상태를 감지해 멈춘다 (`MatlabFolder.HasUnsavedMark`). 남은 것은 read 시점 대비 stale 검사다.
- **9번 루프**: 도구는 이미 순차 실행한다. 답 없는 호출은 `CloseDanglingToolCalls`가 채우고, 빠진 ID는 `call_{step}_{index}`로 만든다. 남은 것은 결과 정리, 한도 직전 경고, 텍스트만 답할 때의 재촉이다.
- **9번 Responses API (확인된 공백)**: `ResponsesProvider`는 `store: false`로 보내지만 `include: ["reasoning.encrypted_content"]` 요청도, reasoning item 재전송도 없다. ChatGPT 경로에서는 도구 호출 단계마다 이전 추론이 버려진다. 우선순위를 높일 만하다.

## 권고 구현 순서
1 → 2 → 3 → 4 → 5를 먼저 한다(모두 S, 서로 독립적, 최근 실패를 직접 해결). 이어서 7(stale 검사)과 6(체크포인트), 9(루프 견고성). 8(정적 검사)은 환경 확인 후 선택한다. 10은 5번과 함께 하면 된다.

## 출처
1차 자료 [P]
- Aider, unified diffs 벤치마크: https://aider.chat/docs/unified-diffs.html
- Aider, edit formats: https://aider.chat/docs/more/edit-formats.html
- Aider, git 통합(auto-commit, /undo): https://aider.chat/docs/git.html
- Anthropic, Writing tools for agents: https://www.anthropic.com/engineering/writing-tools-for-agents
- Anthropic, Building effective agents: https://www.anthropic.com/engineering/building-effective-agents
- Anthropic, Effective context engineering for AI agents: https://www.anthropic.com/engineering/effective-context-engineering-for-ai-agents
- Anthropic, Text editor tool 문서: https://platform.claude.com/docs/en/agents-and-tools/tool-use/text-editor-tool
- Claude Code, Checkpointing: https://code.claude.com/docs/en/checkpointing
- Cline, Checkpoints: https://docs.cline.bot/features/checkpoints
- OpenAI, Reasoning guide (reasoning items와 function calling): https://developers.openai.com/api/docs/guides/reasoning
- OpenAI Codex CLI 프롬프트: https://github.com/openai/codex/blob/main/codex-rs/core/gpt_5_codex_prompt.md
- SWE-agent ACI 논문(초록만 확인): https://arxiv.org/abs/2405.15793
- Mario Zechner, pi coding agent 설계 글: https://mariozechner.at/posts/2025-11-30-pi-coding-agent/
- pi-mono 저장소(철학 부분만 확인): https://github.com/badlogic/pi-mono/tree/main/packages/coding-agent
- MathWorks MATLAB MCP Core Server: https://github.com/matlab/matlab-mcp-core-server
- MathWorks checkcode 문서: https://www.mathworks.com/help/matlab/ref/checkcode.html
- GNU Octave 명령행 옵션: https://docs.octave.org/v9.2.0/Command-Line-Options.html

2차 자료 [S] (검색 결과 요약으로만 확인)
- Codex apply_patch instructions: https://github.com/openai/codex/blob/main/codex-rs/apply-patch/apply_patch_tool_instructions.md
- Codex 이슈 #34210 (Move to 같은 경로): https://github.com/openai/codex/issues/34210
- Codex 이슈 #2578: https://github.com/openai/codex/issues/2578
- Codex 이슈 #2030: https://github.com/openai/codex/issues/2030
- opencode edit fuzzy 매칭 이슈/PR: https://github.com/anomalyco/opencode/pull/51707, https://github.com/anomalyco/opencode/issues/1261, https://github.com/anomalyco/opencode/pull/45092
- 편집 방식 비교 블로그: https://kondasamy.com/blog/2026/how-ai-coding-agents-edit-code/

미확인 [U] 항목: 벤더별 tool-call ID 제약, SWE-agent의 편집 후 린트 세부, `checkcode`의 비실행 보장, Octave parse-only 호출 방법, MATLAB Editor 미저장 버퍼 감지, Claude Code의 "modified since read" 오류 문구. 구현 전에 각각 검증할 것.
