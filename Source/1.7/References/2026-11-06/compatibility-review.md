# RimKata — 2026-11-06 설치 빌드 비교

- 보관·검토일: 2026-11-07 (한국 시간).
- 현재 게임: `1.7.5000 rev1275`, Steam BuildID `26468007`.
- Steam 로컬 업데이트 완료: `2026-11-06 06:07:50`. 공개 배포 시각을 뜻하지 않습니다.
- 비교본: `2026-10-27/`, 게임 `1.7.4990 rev1069`. 중간 업데이트 참조본이 없어 차이를 11월 6일 하루 변경으로 한정하지 않습니다.
- 게임 DLL 4개와 현재 Harmony DLL, Version.txt 및 출처·SHA256 manifest를 별도 보관했습니다. Harmony는 10월 27일 보관본과 동일합니다.

## 확인한 범위

- 렌더/UI 23개 타입 483개 메서드의 정규화 IL, 해당 필드·시그니처·매개변수 이름은 동일했습니다. PawnRenderer/RenderTree/RenderUtility, 무기 조준, MeshPool, FloatMenu와 기즈모·창 경계를 포함합니다.
- Unity Mesh/Object/Graphics 및 Waylith Mesh/CpuMeshData/UnityWorld의 관련 구현도 동일합니다. 기존 1.7 메시 독립 복사 대응은 유지합니다.
- Material 복사·병합이 명시적 renderQueue를 보존하도록 수정됐습니다. RimKataBreachDoor.ResolveMaterial의 재질 요청·복사 경로는 새 동작과 일치합니다.
- Assembly-CSharp 전체 메서드 비교에서 전투·Verb·Pather·JobTracker·SkillRecord·Health·Equipment·Stance·Stagger 관련 구현 변경은 발견되지 않았습니다.
- 현재 RK DLL의 Assembly-CSharp 직접 메서드·필드 참조 1,383개는 최신 DLL에서 모두 해석됐습니다(미해결 0).
- 모드 로딩에는 어셈블리 로드 후 알림이 추가됐지만 이후 Mod 생성·Def 로드·RK StaticConstructorOnStartup의 준비 순서는 유지됩니다. RK가 쓰는 WorkshopItems.AllSubscribedItems 목록도 동일합니다. 파일명 정리와 Need_Rest.Resting 경계 수정은 RK 직접 연결 대상이 아닙니다. 별도 호환 수정이 필요한 근거를 발견하지 못했습니다.
- 현재 소스를 이 스냅샷으로 격리 빌드하여 Release 경고 0·오류 0을 확인했습니다. 출력은 Temp/RimKata-20261106-build/out이며 배포 DLL을 교체하지 않았습니다.
- 현재 실행 로그(2026-11-07 06:49 시작)는 동일 게임 버전이고 RimKata `patch_failures=0`, `patch_group_retries=0`입니다. 이것은 최신 소스의 모든 인게임 행동을 검증했다는 뜻은 아닙니다.

기본 References, 이전 10월 27일 참조본, 프로젝트, 양 버전 제품 DLL의 비교 전후 16개 파일 해시가 일치합니다. 인게임 전투·GPU 출력·전체 모드 조합 검증은 실행하지 않았습니다.
