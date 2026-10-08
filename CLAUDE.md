# BullsEye — 에이전트 작업 규칙

## 유료 API 호출

- OpenRouter 실제 API를 부르는 작업(`mise run smoke`, curl/스크립트로 직접 요청 등)은 **실행 전에 반드시 사용자에게 허락을 받는다.**
  어떤 모델로 몇 번 보낼지, 왜 필요한지를 먼저 말한다. 비싼 모델(`-pro` 등)은 특히 따로 짚는다.
- 모델 목록 조회(`GET /api/v1/models`)처럼 과금되지 않는 호출은 허락 없이 해도 된다.
- 재현은 가능하면 단위 테스트와 가짜 provider(`BULLSEYE_MOCK=1`)로 먼저 한다.
