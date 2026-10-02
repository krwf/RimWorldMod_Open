# RimKata 최적화 기록 — 2026-09-01 ~ 2026-10-01

작성일: 2026-10-01. 기존에 해결한 비용과 실행 구조를 새 기능에서 다시 만들지 않기 위한 발췌본입니다.

주 원문은 `RimKata_code_cleanup_worklog_260830.md`입니다. 기간 안의 날짜가 확인되는 기록에서 최적화·성능 조사·후속 폐기 결정만 추렸습니다. 같은 문제의 반복 기록은 묶었으며, 기능 설명·번역·기본값·메서드 전수 목록·반복 빌드 보고는 제외했습니다. 날짜가 없는 8월 말~9월 초 경계 기록은 임의로 9월에 편입하지 않았습니다. 원문 링크의 줄 번호는 작성 시점 기준입니다.

여기서 **구현**은 해당 날짜의 작업 기록에 구현됐다는 뜻입니다. 모든 항목을 현재 소스와 인게임에서 다시 검증했다는 뜻은 아닙니다. 이후 기록이 앞선 방식을 폐기·대체했다면 마지막 결정을 함께 적었습니다. 10월 1일의 현재 회귀 검토는 맨 뒤에 따로 구분했습니다.

## 1. 새 작업 전에 적용할 핵심 기준

1. **공격 요청과 실제 실행을 분리합니다.** 본체는 재사용하는 요청에 무기·대상·문맥을 담아 `RimKataNativeAttack.Queue()`로 등록하고 복귀합니다. 실제 무기 소유자의 `VerbTick`이 실행합니다. 특수 기능이라는 이유로 별도 함수에서 `WarmupComplete → 피해 처리 → 반환 → 다음 무기`를 다시 만들지 않습니다. [실행 위임 기록][native]
2. **일반 근접 사격의 직접 피해·즉시 Impact 전달은 폐기된 방식입니다.** 발사 시 필요한 명중·방어 문맥을 기록하고 투사체의 원래 충돌 처리가 소비합니다. 당시 `PrepareImmediateImpact`는 기존 요격 용도로만 남겼습니다. 이 과거 코드를 새로운 일반·특수 공격의 표준으로 복사하지 않습니다. [폐기 기록][close-native]
3. **평시에는 일반 폰의 장비·자격·전투 상태를 새 기능 때문에 반복 확인하지 않습니다.** 연출을 시작하는 자격자가 이미 아는 본인·상대를 시작 사건에서 등록하고, 진행 중인 참가자만 갱신하며, 종료 사건에서 해제합니다. 회피는 자격자 본인의 연출입니다. 비자격자도 지목된 쳐내기 상대·제압 대상일 수 있다는 예외를 일반 폰의 회피 검사로 확대하지 않습니다. **전체 순회문이 없어도 모든 폰의 렌더 호출에서 자격·상태를 조회하면 전체 폰에 반복 비용이 듭니다.** [일반 폰 진입][ordinary] · [참가자 공유][participant] · [등록 자료 직접 소비](#registered-visual-source)
4. **전투 상태 존재 캐시를 유지합니다.** 이를 없애고 자격자 맵 순회로 대체한 시도는 원복됐습니다. 최종 채택한 개선은 기존 캐시와 진입 구조를 보존하면서 이미 얻은 `state`·`owner`를 전달하는 것입니다. [원복과 최종 결정][presence]
5. **같은 호출에서 이미 확정한 값을 전달합니다.** 무기 바인딩·허용·자격·적대·Touch·사거리·가용성·빈 후보 선택을 하위 함수마다 다시 해결하지 않습니다. 다만 공격·장비·Job 콜백으로 상태가 바뀐 뒤에는 이전 값을 그대로 쓰지 않습니다. [바인딩][binding] · [같은 처리 결과 재사용][candidate-pass]
6. **림카타가 전투를 인계받으면 바닐라의 중복 원거리 탐색을 탐색 전에 차단합니다.** 바닐라가 전부 탐색한 뒤 발사만 막는 것으로 끝내지 않습니다. 전투 종료·자격 상실·일시 비활성 시 복귀와 기존 근접 대응·소화는 보존합니다. [중복 탐색 차단][wait-search]
7. **주·부 무기는 한 번의 공용 탐색을 공유합니다.** 기하 수집과 후보 검증을 분산하고, 검증된 일반 후보를 반복 입장 심사하거나 전체 후보를 한꺼번에 재검증하지 않습니다. 새 특수 연출에 고리 탐색을 복제하지 않습니다. [후보 신뢰][candidate-trust] · [분산 처리][ring-concurrent]
8. **렌더 소비자는 사건에서 등록한 자료를 읽습니다.** 값 스냅샷 또는 이미 확보한 `state`·`owner`와 자격 분류를 전달하고, 렌더에서 소유자·자격·상태 사전을 다시 찾아 들어가지 않습니다. 진행률은 등록된 상태를 기존 읽기 잠금 안에서 읽어 현재 값을 얻습니다. 비참가 결과도 해당 렌더 문맥에 보관해 하위 기능이 다시 조회하지 않게 하고, 중첩 일반 폰·초상화·예외 때 부모 문맥을 복원합니다. [읽기 전용 렌더][render-readonly] · [참가 문맥][participant] · [등록 자료 직접 소비](#registered-visual-source)
9. **고정 데이터와 파일은 시작·설정·생성·저장/로드 사건에서 준비합니다.** 발사·틱·Draw 중 XML/디스크를 다시 읽지 않습니다. 실제 모드 데이터 타입과 무기 정체성, 동적으로 변하는 능력치의 의미는 보존합니다. [무기 준비][prepared14] · [문 준비][door]
10. **캐시는 유지 비용까지 포함해 판단합니다.** 일회성 셀 리스트 조회를 줄이려고 모든 이동의 갱신 비용을 늘리는 방식은 이미 폐기됐습니다. 같은 틱/호출 한정 재사용과 이벤트 무효화를 우선하며, 동적 스탯·동적 적대 결과를 근거 없이 장기 고정하지 않습니다. [점유 마스크 폐기][occupancy] · [동적 적대 경계][neutral]

## 2. 공격 실행·전투 제어

| 날짜 | 추출한 최적화와 보존 조건 | 상태·근거 |
|---|---|---|
| 09-05 | 전용 공격 Job이 확보한 전투 상태를 근접 문맥·연속성·발사 처리에 전달하여 중복 상태 조회를 줄였습니다. 서로 다른 Job 표적과 근접 표적은 구분합니다. | 구현. [원문][job-state] |
| 09-06 | JobTracker와 전용 JobDriver가 같은 `TickCombat`으로 진입하고 준비는 한 번만 합니다. 소집 여부는 같은 처리기 안의 명령·기능 권한이며 별도 전투 엔진을 고르는 조건이 아닙니다. 전용 Job을 JobTracker가 다시 구동하지 않습니다. | 구현. 초기 소집 전용 진입 제한은 후속 정정됨. [원문][controller] |
| 09-06 | 근접 문맥 정리 결과와 이미 확보한 상태를 후속 호출에 전달합니다. 기존 사이클 작업만으로 연속성이 확정되면 무기 도달·명중 가능 검사를 생략합니다. | 구현. [원문][continuity6] |
| 09-06 | 계속 조준하는 중간에 불필요한 `Stance_Mobile`을 거치지 않도록 했습니다. Mobile 알림이 일반 대기 Job의 `CheckForAutoAttack`을 불러 표적 점수·사선·스탯 계산으로 이어졌기 때문입니다. 마지막 실제 자세 정리는 유지합니다. | 원칙 유지. 당시 `FireSingleShot` 내부 복원 구현은 09-09 실행 위임으로 대체. [원문][mobile] |
| 09-08 | 공격 진입에서 owner가 없고 비소집인 일반 폰은 정신 상태·Job·권한 검사를 하기 전에 반환합니다. 기존 상태는 한 번 구해 전달하고, 후속 Job 예약을 위한 중복 캐시는 제거했습니다. | 구현. [원문][ordinary] |
| 09-08 | 장비·자격·설정·Verb 변경에서 바인딩을 dirty로 표시하고, 깨끗한 revision이면 재해결하지 않습니다. 주·부 슬롯의 무기/Verb/허용과 빈 슬롯까지 기록합니다. 빈 슬롯은 정규화·타이머·시각 목표·실행을 생략하며 공유 탐색과 몸 조준은 폰당 한 번 처리합니다. | 구현. [원문][binding] |
| 09-08 | 이미 얻은 장비·state·bound Verb를 전달합니다. 설정/로드 정규화는 실제 등록된 폰·무기 쌍을 사용하고 일반 폰의 장비를 다시 발견하지 않습니다. 자격·spawn을 장비 getter보다 먼저 확인합니다. | 구현. [원문][loadout] |
| 09-08 | 근접 진입에서 availability·Touch를 재사용하고 전체 후보 재검증을 제거했습니다. 이미 아는 근접 대상은 직접 후보로 넣으며, 근접 쿨다운 진입에 추가 고리 탐색을 시작하지 않습니다. 공격 콜백 이후에는 변경된 문맥을 재확인합니다. 회피 연출 중에도 기존 타이머는 진행합니다. | 구현. [원문][close-entry] |
| 09-08 | 방어 적용 틱에 새 대응 쿨다운을 다시 감소시키거나 같은 슬롯의 후보 준비·선정·공격을 재진입시키지 않습니다. 다른 손과 추가 방어는 독립적으로 유지합니다. | 구현. [원문][response-tick] |
| 09-08 | 피격 알림의 자격·적대 중복 wrapper와 쓰지 않는 map component 조회를 제거했습니다. 같은 호출의 무작위 공격 설정값을 양손 후보·연속성 갱신에 전달합니다. | 구현. [원문][defense-reuse] |
| 09-09 | 무기 사이클마다 요청 객체를 재사용하여 대상·문맥만 등록하고 복귀합니다. 바닐라 무기 소유자의 `VerbTick`이 실제 실행하며 완료 경로에서 실행 여부를 한 번 소비합니다. 동기식 `FireSingleShot`, 매 발사 반사 읽기 6회/쓰기 12회, reset/restore와 첫 슬롯 반환 후 별도 재검사를 제거했습니다. | 채택한 실행 구조. [원문][native] |
| 09-09 | 일반 근접 사격의 직접 `TakeDamage`·강제 즉시 `Impact`를 제거했습니다. 원래 투사체를 발사하고, 발사 시 판정된 방어 결과를 충돌까지 보존하여 같은 공격의 방어·연출·쿨다운을 반복 적용하지 않습니다. | 직접 피해 경로 폐기. [원문][close-native] |
| 09-10 | 검색·이동·회피·pending 플래그로 전투 연속성이 이미 참이면 근접 상대의 적대·상태·Touch 검사를 생략하도록 순서를 바꿨습니다. OR 조건의 의미와 상태 변경 시점은 유지했습니다. | 구현. [원문][continuity10] |
| 09-10 | 정규화→연속성→슬롯 실행은 같은 처리의 바인딩/가용성 결과를 공유합니다. 양손 조준 ETA를 비교할 때 같은 caster의 조준 지연 스탯은 그 결정에서 한 번만 읽습니다. 실제 조준 시작과 쿨다운의 live 스탯·반올림은 유지합니다. | 구현. 장기 스탯 캐시 아님. [원문][ring-concurrent] |
| 09-14 | 단발 전환 OFF에서는 원본 데이터와 원래 점사를 사용합니다. 공격 요청 하나가 점사 전체를 소유하도록 하여 수동 잔여 발수·간격 타이머를 제거했습니다. 발사별 방어 문맥은 유지하고 완료·쿨다운은 점사 끝에 처리합니다. | 09-09 실행 위임의 후속 확장. [원문][prepared14] |
| 09-17 | 명령·사격 허용 기즈모에서 조준 갱신을 직접 실행하지 않고 기존 전투 틱이 소비하게 했습니다. 이미 state를 가진 경로는 그 참조로 `TryGetNextAim`을 호출합니다. | 구현. [원문][command-aim] |
| 09-26 | 실제 대기 자세로 끝나고 일반 공격 작업이 없을 때 기존 자세 만료·해제 사건에서 일반 후보를 정리합니다. 쿨다운·예약된 링 후보·이동 적 감시·폭발체 후보는 보존하며 별도 상시 검사를 추가하지 않습니다. | 구현. [원문][idle-candidates] |
| 09-27 | 돌파 도착 대기에서 바닐라가 4틱마다 탐색한 뒤 발사만 거절되던 경로를, 해당 Wait_Combat의 원거리 허용값을 일시 보류하여 탐색 입구에서 막았습니다. 원래 값과 Job ID를 종료·로드 때 복원합니다. | 구현. 돌파 대기 한정 조치. [원문][breach-wait] |
| 09-28 | 림카타 인계 후 일반 Wait에서도 원거리 표적 탐색 전에 기존 소유 캐시·전투 연속성/쿨다운을 확인해 차단합니다. 기존 근접 대응·소화·사냥 경계와 종료 후 복귀를 유지하며 새 틱 콜백이나 별도 탐색은 만들지 않았습니다. | 구현. [원문][wait-search] |

## 3. 고리 탐색·후보 검증

| 날짜 | 추출한 최적화와 보존 조건 | 상태·근거 |
|---|---|---|
| 09-04 | 활성 폭발체 집합의 Count가 0이면 요격용 Verb·사거리·교차 검증과 idle 탐색 요청에 들어가지 않습니다. 기존 생성/발사/해제 사건이 관리하는 집합을 재사용합니다. | 구현. [원문][explosive-empty] |
| 09-05→07 | 무후보 이동의 휴면 조건을 정리하고, 이후 이동 전 `GetPotentialTargetsFor → HostileTo` 맵 잠재 표적 선조회를 제거했습니다. 빈 검색을 같은 셀에서 새 출발로 반복하지 않으며 기존 권한과 공용 링 탐색을 사용합니다. | 앞선 선조회 방식은 후속 대체. [09-05][move-gate5] · [09-07][move-gate7] |
| 09-06→07→10 | 도주 적 전달은 전투 종료/속도 제한 해제 경계에서 얻은 잔존 적대 집합과 이동 사건을 사용합니다. 동일 틱의 알림은 묶고 실제 이동 수신자에게 한 번씩 전달합니다. 이후 수신자가 없으면 감시 참조·알림을 해제하고, 표시된 감시만 재개하도록 보강했습니다. | 단계별 후속 대체. [배치][moving-batch] · [수명][movement-watch] · [최종 보강][ring-concurrent] |
| 09-07 | 새 후보만 적대·허용 등 전체 입장 조건을 검증합니다. 등록 후보는 생존/전투불능·현재 범위·실제 사격/Touch 등 필요한 조건만 확인하며 계획 승격 때 입장 검증을 되풀이하지 않습니다. | 구현. [원문][candidate-trust] |
| 09-07 | 일반 예약이 쿨다운 >1 구간이면 반복 계획 검증을 생략하고, 1/0·조준 중·실제 실행 경계는 보존합니다. 후보 탈락은 진행 중 링·원점·상한 예약을 통째로 초기화하지 않습니다. | 구현. [원문][candidate-trust] |
| 09-07 | 정상 탐색 완료 시 각 슬롯의 저장 후보 한 명씩 순차 사격 가능 확인을 합니다. 전체 후보를 한꺼번에 재검증하거나, 링 수집 중 이미 등록된 후보를 다시 입장 심사하지 않습니다. | 구현. [원문][ring-maintenance] |
| 09-07 | 조준 대상의 셀 이동을 고리 탐색 시작 조건에서 제외했습니다. 실제 사격 불가→후보 퇴출이 기존 보충 경로를 사용합니다. | 구현. [원문][target-move] |
| 09-07 | 빈 슬롯·쿨다운만 남은 슬롯은 가용성 검사 전에 빠지고, 링 처리의 슬롯 메타데이터와 대상 거리·이동 범위 결과를 같은 호출에서 공유합니다. 셀마다 Pawn.Map을 다시 읽지 않습니다. | 구현. [원문][prepared-pass] |
| 09-08→09 | 링/디스크 기하를 사전 계산해 사용합니다. 이후 8방향 시작점·고정 회전 방향의 분할 순회로 발전했습니다. 반경 12셀 이하는 1틱, 25셀 이하는 2틱, 40셀 이하는 3틱, 그 밖은 기하 셀 96개/틱의 당시 분산 규칙을 유지했습니다. | 09-08 행 순회 세부는 09-09 분할 순회로 대체. [기하][geometry] · [분산][close-native] |
| 09-09 | 자동 일반 후보는 Pawn만 받습니다. 슬롯별 Pawn ID 집합으로 등록 여부를 빠르게 확인하고 타입·닫힌 슬롯·기등록 여부를 거리/입장 검증보다 먼저 봅니다. 플레이어 지정 대상과 별도 폭발체 공급은 유지합니다. | 구현. [원문][candidate-pass] |
| 09-09 | 선택 과정은 state가 가진 owner component를 재사용합니다. 후보도 폭발체도 없어 실패한 빈 선택과 성공한 사격 가능 결과는 같은 대상·Verb·문맥인 처리 안에서 재사용합니다. 새 공급·명령·장비·공격 콜백은 재사용을 무효화합니다. | 구현. 틱을 넘는 결과 고정 아님. [원문][candidate-pass] |
| 09-09 | Pawn 점유 bitmask와 매 이동 등록/해제 훅을 제거하고, 방문 셀의 native Thing list를 직접 읽도록 복귀했습니다. 비Pawn은 타입 검사에서 끝내고 별도 유지 캐시를 추가하지 않았습니다. | 유지 비용 때문에 폐기한 최적화 시도. [원문][occupancy] |
| 09-09→10 | 완성된 각 링마다 참여 슬롯별로 틱당 후보 한 명을 무복원 추첨·검증합니다. 전체 링에 걸친 전역 한 명 상한은 아닙니다. 거절됐다고 같은 틱에 즉시 재추첨하지 않습니다. 09-10에는 다음 기하 수집과 이전 링 검증을 동시에 진행하고 저장 후보+검증 대기 수로 확장을 조절했습니다. 한 슬롯이 닫혀도 다른 슬롯은 계속합니다. | 구현·후속 보강. [09-09][close-native] · [09-10][ring-concurrent] |
| 09-10 | 외부 이동 적도 같은 지연 검증 과정에 넣습니다. 이동 알림에서 전체 진입 검증을 동기로 수행한 뒤 후보 처리에서 다시 검증하던 중복을 없애고, 범위는 알림 처리당 한 번 해결합니다. | 구현. [원문][ring-concurrent] |
| 09-10 | 죽음·전투 불능을 HostileTo보다 먼저, pending 대상의 다운 상태를 invisibility 헤디프 검사보다 먼저 봅니다. 실제 전투 가능한 기어 사격 대상은 유지합니다. | 구현. [원문][incapacitated] |
| 09-10 | 탐색자별로 확정 비적대 ID만 임시 보관하여 재입장·적대 검사를 줄입니다. 표적/세력/관계 사건의 revision으로 지연 무효화합니다. 거리 등 알림 없이 바뀌는 동적 적대 규칙은 저장하지 않습니다. 수면/dormancy는 기존 driver/component 값으로 먼저 거절합니다. | 구현. 전역 적대 캐시·수면 폴링 없음. [원문][neutral] |

## 4. 일반 폰 배제·사건 기반 갱신

| 날짜 | 추출한 최적화와 보존 조건 | 상태·근거 |
|---|---|---|
| 09-03 | HUD가 꺼지면 해당 GUI 컴포넌트를 토글 사건에서 등록 해제하여 빈 GUI/Tick/Update 호출을 없앴습니다. 범위 표시용 Mesh/Material은 실제 활성 렌더에서 지연 생성·재사용하며, 비개발자 모드 기즈모는 불필요한 iterator를 만들지 않습니다. | 구현. [원문][hud] |
| 09-04 | 날씨 상한의 무조건 매 틱 비교를 `WeatherManager.TransitionTo` 사건으로 옮기고 실제 값 변경에만 range revision을 올립니다. 초기화·필요 시 검증은 유지합니다. | 구현. 사용자가 기상별 사거리 변화를 확인한 기록 있음. [원문][weather] |
| 09-04 | 임시 비활성 cache 조회 때마다 집합에 재등록하지 않습니다. 진입/복구 사건이 정리 요청을 남기고 기존 상태 루프에서 한 번 소비합니다. 중복 정리 호출 9곳을 제거하고 비활성 집합만 복구 확인합니다. | 구현. 진행 중 타이머·연출 관리는 제거 대상이 아님. [원문][weather] |
| 09-04 | 유휴 폭발체 깨우기 순회는 기존 플레이어 조건과 정식 자격을 통과한 폰으로 제한합니다. `CanAcceptIdleProjectileSearch`와 실제 Queue의 동일 틱 진입 검사를 합쳐 이미 얻은 state를 재사용합니다. | 구현. 후속 수신자 사전 필터로 보강. [원문][idle-intercept] |
| 09-04 | 폭발체 교차 예측은 후보 추가·조준 시작·실제 명중 분류 탄의 Launch에서만 수행합니다. 조준 중 ValidPlan 매 틱과 비행 중 재계산을 피하고 기존 사거리 제곱과 참조를 재사용합니다. | 구현. 사용자가 예측 요격 동작 확인. [원문][intercept-prediction] |
| 09-04 | 비Pawn 공격자의 임시 자동공격 요청 묶음을 목표·잔여틱·매 틱 검증/감소·HUD·저장/복원·후속 소비까지 제거했습니다. 정상 공용 후보/Job 경로는 유지합니다. | 제거 완료. 대체 캐시 없음. [원문][temporary-request] |
| 09-04 | AI 폭발체 깨우기는 기존 Job giver·Lord/duty가 전투 참여를 나타낼 때 기존 사건 목록에 포함합니다. 준비·배회 AI를 제외하고 이미 조준/점사/전투 중인 경우 새 깨우기 요청을 만들지 않습니다. | 구현. [원문][ai-projectile] |
| 09-05 | 무상태·무이동 소집 폰은 상태 조회 후 휴면하고, 이동도 끝난 빈 상태는 기존 추적을 해제합니다. 기존 활성 전투의 타이머·탐색·발사는 유지합니다. | 구현. 이후 공용 진입으로 통합. [원문][dormancy] |
| 09-05 | 전투 정상 속도 요청은 같은 TickManager·틱에서 한 번으로 합칩니다. 실제 표적 작업 없는 사이클의 Verb 조회도 생략하며, 별도 전투 bitset/수동 활성 목록은 동기화 부담 때문에 채택하지 않았습니다. | 구현 및 비채택 경계. [원문][dormancy] |
| 09-05 | DraftedFire에서 전용 Job·정지 무상태를 먼저 구분하고 공유 탐색 후 engagement와 해석한 근접 표적을 같은 틱에 전달합니다. 중간 상태가 바뀔 수 있는 연속성/사용 무기 판정을 무조건 합치는 안은 보류했습니다. | 구현·보류 범위 구분. [원문][drafted-reuse] |
| 09-06 | 폭발체 깨우기는 자격·작업·장비 조건을 통과한 수신자만 사용하고 실제 후보가 있을 때 상태를 생성합니다. 평시 수신자 전체를 매 틱 다시 발견하지 않습니다. | 구현. [원문][projectile-recipients] |
| 09-06 | 임시 비활성 공용 관문을 통과한 동일 호출의 정신이상·활성 자격 재검사를 제거합니다. 전역 JobTracker는 일반 폰의 캐시를 만들지 않는 직접 선행 검사를 유지하고 내부에 검증값을 전달합니다. | 구현. 화재 취소 등 부수효과는 보존. [원문][inactivity-gate] |
| 09-10 | 초기화/스폰/자격원/세력/설정 사건에서 맵별 실효 자격자 집합을 게시합니다. 일반 권한 갱신은 등록 집합만 사용하며, 제한 해제처럼 범위가 실제로 바뀌는 사건은 일회성 재구축을 허용합니다. | 구현. 매 틱 자격자 재발견과 구분. [원문][ring-concurrent] |
| 09-14→15 | PresenceCache 제거·맵 자격자 순회 대체는 전체 비용 증가 보고 후 원복했습니다. 최종적으로 기존 캐시·JobTracker 진입을 유지하고 이미 준비한 state/owner만 공유했습니다. | 삭제 시도 폐기, 좁은 재사용 개선 채택. [원문][presence] |
| 09-14 | 폭발성 투사체 회피는 기존 pending validation에 발사+1틱 예약으로 연결하고, 짧은 비행도 기존 Impact에서 예약 결과를 한 번 소비합니다. 일반 폰 탐색이나 새 투사체 Tick 패치를 추가하지 않습니다. | 구현. [원문][explosive-scheduler] |
| 09-15 | 폭발 반경이 없는 실제 Bullet을 발사 시 폭발체 회피와 실제 피격자 방어에서 중복 굴리지 않도록 구분했습니다. 같은 Impact의 방어 결과는 재사용합니다. | 구현. [원문][bullet-defense] |
| 09-19 | 적대 AI의 idle 전투 진입은 비강제 Wait_Wander/GotoWander 시작 사건과 기존 자격·잠재 표적 캐시를 사용합니다. 전체 폰의 매 틱 탐색을 추가하지 않습니다. | 구현. [원문][idle-ai] |
| 09-25 | 넘어짐은 실제 근접 회피/빗나감, 엎드림은 조준·사냥 조준·활성 표적 이동 사건으로 진입합니다. 일반 전투 순회의 자세 갱신 조건을 제거하고 등록된 별도 연출 목록만 갱신합니다. | 구현. [원문][ground-events] |
| 09-25→26 | DAC 호환은 해당 모드가 로드된 경우에만 등록하며 같은 공격의 방어 성공/실패를 재사용합니다. PocketSand도 교체 실행 사건 한 곳에 연결하고 기존 임시 교체 기록을 사용하여 상시 폰 검사를 추가하지 않습니다. | 구현. [DAC][compat-defense] · [PocketSand][compat-pocket] |
| 09-25 | 기어 사격은 실제 기어 이동 참가자만 갱신하고 프로필 전환·정지·탈락 사건에서 해제합니다. CE·Muzzle Flash는 설치 시에만 연결하고 렌더·발사 좌표를 공유합니다. | 구현. [원문][crawl-events] |
| 09-26 | 사냥의 추가 무기는 현재 발사 Toil 사건에 연결합니다. 사냥 은폐는 기존 반격 능력치 조회 한 곳에서 현재 엎드린 참가자만 처리합니다. | 구현. [사냥][idle-candidates] · [은폐][hunting-conceal] |
| 09-27 | 돌파 자격 상실·화재·정신 상태·기절 해제는 기존 EligibilityCache/TemporaryInactivity 알림에 연결했습니다. 중복 돌파 `Fire.AttachTo` 패치는 제거했습니다. | 구현. [원문][breach-access] |
| 09-28 | 동물 탈출·공병의 문 채굴 돌파는 실제 `JobDriver_Mine.DoDamage` 사건에서 문 타입·캐시 자격을 먼저 봅니다. 일반 채굴·벽·암석은 즉시 반환합니다. | 구현. [원문][mine-event] |
| 09-29 | 제압은 실제 참가자만 활성 목록에서 갱신하고, 운반 내용 변경·기존 자격 상실/비활성 알림에서 정리합니다. 기존 공통 참가자 캐시와 특수 무기 렌더 탐색 결과를 재사용합니다. | 당시 최초 구현의 최적화 경계. 후속 제압 기능 전체 검증을 뜻하지 않음. [원문][subdue] |

## 5. 렌더·UI 중복 작업

| 날짜 | 추출한 최적화와 보존 조건 | 상태·근거 |
|---|---|---|
| 09-05 | GunReady의 평시 장비 조회를 줄이고, 실제 보조무기·쳐내기 참가·준비 자세 후보가 없으면 활성 문맥을 만들지 않습니다. 비참가 문맥 표식은 하위 fallback 재조회를 막는 데 사용합니다. | 구현. 09-06·09-28 후속 공유 개선과 함께 적용. [원문][gunready5] |
| 09-05 | PairRange/SquadRange가 이미 얻은 자격·무기·사거리를 재사용합니다. CombatIndicators는 상태 표식이나 실제 표시 가능한 바닐라 쿨다운이 없으면 장비 조회 전에 반환하며 같은 프레임 상태를 공유합니다. | 구현. [범위][range-reuse] · [표시][indicators] |
| 09-05 | 근접 공격 기즈모 Prefix의 자격 결과를 Postfix로 전달해 정상 경로의 자격 검사 두 번을 한 번으로 줄였습니다. 앞선 패치가 Prefix 실행을 생략했을 때만 원래 fallback을 수행합니다. | 구현. [원문][melee-gizmo] |
| 09-05→07 | DodgeOffset은 상태 표식을 Pawn.Map보다 먼저 확인하고, hit 때 얻은 소유 map component를 스냅샷 조회에 전달합니다. 무상태 일반 폰은 장비·lean·map component 재조회로 들어가지 않습니다. | 구현. [09-05][dodge-render] · [09-07][render-owner] |
| 09-06 | GunReady와 CarryDraw의 큰 구조체 문맥 복사를 줄였습니다. 작은 복원 토큰·재사용 중첩 스택을 사용하고, 하위 소비자는 읽기 전용 참조를 사용합니다. 예외·중첩·토큰 불일치의 복원 경계는 유지합니다. | 구현. [GunReady][gunready-copy] · [Carry][carry-copy] |
| 09-06 | 표준 Verb 명령 생성마다 실행되던 비소집 부무기 Gizmo Postfix를 제거하고 기존 장비 Gizmo 분류 경로로 합쳤습니다. 한 열거에서 자격·부무기·필요한 번역을 공유하며 통합 다중선택에서 버릴 명령은 사전 작업도 생략합니다. | 구현. [원문][gizmo] |
| 09-06 | 부무기 registry의 같은 폰·같은 틱 조회는 성공과 null 결과를 모두 공유합니다. Set은 즉시 갱신하고 RemoveAt/로드는 무효화합니다. 새 장기 인덱스나 조회마다 잠금을 추가하지 않습니다. | 구현. [원문][secondary-tick] |
| 09-06 | 장비 렌더 문맥에서 동일 Pawn의 무표적 PairRange 호출은 원래 선택을 사용합니다. 실제 표적이 있는 공격·렌더 밖 AI/사냥 호출은 기존 경로를 유지합니다. | 구현. [원문][render-pairrange] |
| 09-06 | 렌더 getter가 `visualTarget`을 정리하던 쓰기 동작을 전투/idle 유지관리로 옮겼습니다. 렌더는 게시된 상태를 읽고 새 타이머·캐시·컬렉션을 만들지 않습니다. | 구현. [원문][render-readonly] |
| 09-06 | 정착민 바 전투 아이콘은 presence 관문 뒤 캐시된 실제 전투 bool만 읽습니다. UI에서 전투 연속성을 다시 계산하거나 전투 상태를 생성하지 않습니다. | 구현. [원문][combat-icon] |
| 09-09 | 정착민 바 부무기 아이콘은 장비·자격·세력·설정 사건에서 표시 쌍을 게시합니다. Draw는 준비된 dictionary만 읽고 소유자·자격·장비 목록·그립을 재검증하지 않습니다. 원래 주무기 아이콘 호출은 유지합니다. | 구현. [원문][colonist-icon] |
| 09-15 | GunReady가 presence와 owner를 한 번 확보하여 대응 스냅샷과 준비 표적 조회에 공유합니다. 캐시를 없애거나 일반 폰에서 실제 장비를 재조회하는 방식은 사용하지 않습니다. | PresenceCache 원복 뒤 채택. [원문][presence] |
| 09-26→28 | 부드러운 조준 전환은 공격 완료/후보 변경 때 시작각·남은 쿨다운을 기록합니다. 최대 24틱 제한은 기존 시작/현재 쿨다운 차이로 계산하며 별도 타이머·틱 검사·저장 필드를 만들지 않습니다. | 구현. [전환 등록][smooth-start] · [기존 값 재사용][smooth24] |
| 09-26 | 엎드림·기어 사격의 조준/쿨다운 표시는 같은 화면 프레임의 실제 무기 최종 제출 좌표를 읽습니다. 원래 표시와 전용 표시가 중복되지 않도록 소유권을 구분합니다. | 구현. [원문][ground-indicator] |
| 09-27 | 돌파 대기 무기는 전용 `RimKataBreachWeaponRender`가 준비된 스냅샷으로 출력합니다. DrawCarriedWeapon·DrawEquipmentAiming·일반 쌍수 렌더에 재진입하지 않으며, 돌파 자세를 소유하는 구간의 중복 무기/부속 출력만 막습니다. 실제 공격은 기존 전투 렌더를 사용합니다. | 앞선 바닐라/일반 대기 경유 구현을 대체. [원문][breach-render] |
| 09-28 | 기존 공통 신체 참가자 캐시에 눕기·기어 사격·돌파 입력을 게시합니다. 시작/변경/종료만 자기 필드를 갱신하고 동일 입력은 재할당하지 않습니다. 일반 전투 정리는 독립 연출의 자료를 임의로 지우지 않습니다. | 구현. [원문][participant] |
| 09-28 | 신체/장비 진입에서 참가·비참가와 스냅샷을 한 번 구해 하위 기능이 공유합니다. 일반 폰은 기능별 Frames/Aims/돌파 registry로 다시 들어가지 않습니다. 머리의 비참가 Draw는 원본 반복문으로 바로 들어갑니다. | 구현. 중첩 폰·초상화·예외 복원 필수. [원문][participant] |
| 09-28 | 돌파·기어 사격의 여러 처리 단계가 같은 참가 판정을 공유하고 등록/해제/게임 교체 revision으로 무효화합니다. 그림자만 그리는 경로도 기존 문맥을 공유하며 쳐내기 자료는 필요한 소비자만 한 번 읽습니다. | 구현. [원문][participant] |
| 09-28 | 돌파 Wait/Released에서는 소비되지 않는 Frame·변환 행렬을 Draw마다 만들지 않습니다. 실제 Slide/Rise만 준비하며 몸 방향·보호·서쪽 무기 출력은 유지합니다. | 구현. [원문][participant] |
| 09-28 | 이전 게임의 연출 참가 항목을 지울 때 이전 폰의 현재 Map을 다시 읽지 않고 게시 항목의 map을 복사합니다. 새 입력을 게시할 때만 현재 Map을 읽습니다. | 참가 캐시 공유 최적화의 후속 회귀 수정까지 보존. [원문][map-cleanup] |

## 6. 사전 준비·파일·수명 관리

| 날짜 | 추출한 최적화와 보존 조건 | 상태·근거 |
|---|---|---|
| 09-09 | 무기 고정 변환 데이터를 시작/설정 무효화에 준비합니다. 실제 공격마다 원본 burst/spacing을 재해석하지 않고 준비 데이터를 사용하되, Pawn/무기의 live 시간 배율은 유지합니다. | 구현. 복사 타입 방식은 09-14에 대체. [원문][native] |
| 09-09 | native 오프닝에서 무조건 Restore→Bind 하던 중복을 없앴습니다. 현재 설정 revision으로 준비돼 있으면 재바인딩하지 않고, 미준비/낡은 데이터만 준비합니다. | 구현. [원문][opening-bind] |
| 09-14 | 무기와 VerbProperties의 실제 타입·모드 private 필드를 보존하여 복사하고 변경되지 않은 참조 데이터는 공유합니다. Bind 중 디스크 접근·전체 Def 변환은 없습니다. 고유 무기 조합은 최초 준비 후 재사용하며 DefDatabase wrapper를 파일 읽기보다 먼저 사용합니다. | 별도 prepared subtype 대체. [원문][prepared14] |
| 09-14 | 저장 대상은 허용된 무기 중 원래 연사가 2발 이상인 비근접 Verb가 있는 경우로 먼저 거릅니다. 불필요한 fingerprint·캐시·파일 작업에 들어가지 않습니다. | 구현. [원문][prepared14] |
| 09-14 | 허용 해제된 준비 무기 파일은 설정 갱신에서 알고 있는 기록만 정리합니다. 단발 전환 OFF는 파일을 유지하며, 매 틱 파일 조회·폴더 스캔은 없습니다. | 이전 허용 해제 시 보존 규칙 대체. [원문][prepared-cleanup] |
| 09-14 | 비인간 장비 카탈로그의 태그·PawnKind·Bossgroup 관계를 한 번 색인하고, 장비 선택 확인의 저장과 런타임 갱신을 분리해 중복 갱신을 줄였습니다. | 구현. [원문][catalog] |
| 09-27 | 문 SpawnSetup 사건으로 생성/건설/재설치/로드된 문만 모아 긴 작업 종료 뒤 정보를 준비합니다. XML 정보와 렌더 recipe는 내용 해시로 중복 제거하고 준비된 조합은 RAM에 보관합니다. 틱·렌더 파일 읽기와 맵/폰 전수 탐색은 없습니다. | 구현. [원문][door] |
| 09-27 | 문 캐시는 게임/모드 환경별로 분리하며 환경 정보는 첫 준비 때 한 번 수집합니다. 이전 게임의 예약 작업은 문 Map 접근 전에 게임 참조로 제외합니다. 별도 GameComponent Tick/Update를 추가하지 않습니다. | 구현. [원문][door-environment] |
| 09-27 | 지원 문의 방향·렌더 층을 한 번 복원하여 메시 출력에 사용합니다. 슬라이딩/렌더 중 파괴된 원본 문이나 디스크를 다시 읽지 않습니다. | 구현. [원문][door-recipe] |
| 09-28 | 문 캐시 정리는 성공한 저장/불러오기 사건에 연결합니다. 세이브의 game/components 끝까지만 읽고 크기·수정시각이 같은 파일은 기존 식별자를 재사용합니다. 환경 캡처를 공유하며 틱·렌더·파일 감시는 없습니다. | 구현. [원문][door-cleanup] |

## 7. 폐기·원복된 방식 — 현행 구현으로 복사하지 않을 것

| 이전 방식 | 마지막 결정과 이유 | 근거 |
|---|---|---|
| 림카타 함수가 `FireSingleShot`으로 직접 발사하고 반환 뒤 상태를 복구·다음 슬롯을 처리 | 09-09 제거. 재사용 요청을 등록하고 실제 native 소유자에게 실행을 위임하는 구조 채택 | [실행 위임][native] |
| 일반 근접 총격의 직접 `TakeDamage` / 발사 후 즉시 `Impact` | 09-09 제거. 원래 투사체 발사·충돌과 저장된 방어 문맥 사용 | [직접 피해 폐기][close-native] |
| 직접 피해용 pending 목록 풀을 일반 공격 구조의 모범으로 재사용 | 목록 풀은 09-08의 국소 개선이었지만, 09-09에 그 목록을 쓰던 직접 피해 큐 자체가 제거됨 | [과거 풀][pending-pool] · [폐기][close-native] |
| Pawn 점유 bitmask와 모든 셀 등록/해제 유지 훅 | 09-09 제거. 실제 방문 셀의 native 리스트 직접 조회로 복귀 | [폐기][occupancy] |
| PresenceCache를 없애고 맵 자격자 순회로 전투를 대체 | 09-15 원복. 캐시·기존 진입은 유지하고 이미 가진 상태/owner만 전달 | [원복][presence] |
| 조준 대상이 움직일 때마다 고리 탐색을 시작 | 09-07 제거. 실제 후보 무효화와 기존 보충 사건 사용 | [대체][target-move] |
| 이동 전 전체 잠재 적대 표적 선조회 / 별도 첫 이동 탐색 | 09-07 제거. 기존 권한·상태 표식·공용 링 및 사건 기반 잔존 적 전달 사용 | [선조회 제거][move-gate7] · [첫 이동 제거][first-move] |
| 09-08 기하 행 순회 또는 09-09 수집/검증의 순차 처리 세부를 최종 방식으로 복사 | 각각 8방향 분할 순회, 09-10 기하 수집과 지연 후보 검증의 동시 진행으로 발전 | [기하][geometry] · [후속][ring-concurrent] |
| 준비 데이터의 별도 `RimKataPreparedVerbProperties` 타입 / 수동 점사 재구성 | 09-14 실제 타입 보존 복사와 native 점사 전체 소유로 대체 | [대체][prepared14] |
| 돌파 대기 무기를 바닐라 대기 출력 또는 일반 쌍수 렌더로 우회 | 09-27 전용 돌파 대기 렌더로 대체. 준비된 참가 스냅샷을 재사용 | [대체][breach-render] |
| 참가 캐시를 지우면서 이전 게임 폰의 현재 `Pawn.Map` 재조회 | 09-28 로드 오류 수정. 이전 게시 map을 보존하고 새 게시 때만 현재 Map 확인 | [후속 수정][map-cleanup] |

## 8. 성능 조사에서 확인한 근거와 해석 경계

- **발사 호출에 피해·사망 처리가 동기로 포함된 기록:** 09-08 표본의 장검 공격은 프레임 23.071ms 중 TakeDamage self 20.952ms, 근접 기관권총은 23.014ms 중 19.916ms였습니다. 09-09 스택 추적으로 `FireSingleShot → native melee TakeDamage → AddHediff → CheckForStateChange → Kill → DropBeforeDying`의 중첩이 확인됐습니다. 본체 실행 위임을 새 기능에서 무시해서는 안 되는 역사적 근거입니다. [계측][damage-measure] · [스택 확인][death-stack]
- **점유 마스크의 조회 감소만으로 이득을 판단하지 않은 기록:** 표본에서 리스트 읽기 98.18%를 생략했어도 별도 유지 비용이 컸습니다. 동일 표본의 warm-loop 환산 이득 약 0.2043ms와 유지 작업 추정 약 46.968ms를 비교한 뒤 제거했습니다. 전체 게임의 보편적 FPS 수치로 확대하지 않습니다. [비교 기록][occupancy-measure]
- **반복 조회의 실제 표본:** 09-08에는 후보가 없고 발사도 없는 프레임에서 슬롯 처리 14회에 선택 28회가 기록됐습니다. 후속 변경은 같은 처리 안의 빈 선택 결과를 재사용하고 새 공급이 들어오면 무효화하는 방향이었습니다. [조사][empty-selection] · [적용][candidate-pass]
- **같은 함수의 비용도 호출 수와 분리해 봅니다:** 09-28 비교의 호출당 39.0→39.6µs, 프레임당 6.2→9.5회는 합계 0.240→0.378ms와 맞았습니다. 이 자료만으로 본문 연산의 회귀나 로드 오류 인과관계를 확정하지 않았습니다. [기록][map-cleanup]
- native 작업을 다른 실행 단계로 넘긴다고 피해·사망 계산 자체가 사라지는 것은 아닙니다. 목적은 본체가 직접 실행을 감싸고 반환에 의존하는 구조와 중복 준비/복구를 제거하는 것입니다. 부모/자식 inclusive 시간을 더하거나 서로 다른 표본을 개선율로 계산하지 않습니다. 빌드·정적 검증은 인게임 성능 확인과 구분합니다. [실행 위임 당시 검증 범위][native]

## 9. 2026-10-01 후속 패치 — 새 기능에 기존 최적화 적용

추출 이후 사용자 승인으로 구현했습니다. 아래는 현재 적용 상태이며 게임 내 재현 결과와 구분합니다. [작업 기록](<C:/Users/user/Documents/RimworldModsFolder/RimKata/RimKata_code_cleanup_worklog_260830.md:3156>)

- **슬라이딩·떨치기:** 직접 WarmupComplete·투사체 수집·즉시 Impact와 전역 Launch 훅 두 개를 제거했습니다. 주·부 슬롯별 재사용 요청을 기존 NativeAttack.Queue/VerbTick에 전달하며, 특수 완료 처리는 일반 후보 재선정과 분리합니다.
- **일정과 수명:** 슬라이드 6틱 공격은 직전 틱에 예약합니다. 떨치기 후속 3틱 간격/24틱 공격은 17절 수정에 따라 해당 틱의 작업 처리에서 셀을 확인하고 같은 틱의 장비 처리로 전달합니다. 떨치기를 발동시킨 기존 공격 자체가 0틱 타격이며, 공격 직전 현재 요청을 인계합니다. 같은 무기의 0틱 공격은 다시 예약하지 않습니다. 쌍수의 반대 무기는 별도로 0틱 요청을 예약합니다. 예약 대상·예정 틱은 저장/로드하며 마지막 요청 완료까지 일반 공격 재개를 막습니다. 연출 종료 뒤 보호 효과는 연장하지 않습니다.
- **참가 판정:** 같은 공격의 중첩 단계는 참가 판정을 공유하고 상태 변경 버전으로 무효화합니다. 특수 요청이 없으면 소유 확인도 CWT 조회 전에 반환합니다. 실제 참가자만 갱신하는 기존 경계는 유지합니다.
- **렌더:** 눕기·돌파·슬라이드 Frame과 몸/무기 측정 자료를 참가자별 저장소에서 값 스냅샷으로 재사용합니다. 게시/읽기 수명과 중첩 문맥을 보존하고 일반 폰은 큰 Frame 복사 전에 빠집니다. 돌파의 같은 시각값은 재게시하지 않습니다.
- **조준:** 단일무기 Cooldown→Ground의 원래 보정 순서/회전 중심을 유지하며 조회·각도를 공유합니다. 이미 가진 state/owner를 전달하고 돌파 대기의 조준 유무는 한 렌더 문맥에서 한 번만 구합니다.
- **검증:** 최종 Release 오류 0·경고 0, diff 검사 통과. 최종 DLL의 CE Queue/Begin/Finish/Cancel transpiler 및 실제 격리 Harmony 패치 생성·등록·해제 검사 20/20 통과. 게임 실행·세이브 재현과 실제 성능 비교는 수행하지 않았습니다.

<a id="registered-visual-source"></a>
## 10. 2026-10-01 참가자 등록 자료를 렌더에서 직접 소비

앞선 참가 캐시 적용만으로 충분하다고 판단하지 않습니다. **등록 표식을 확인한 뒤 렌더 소비자가 다시 자격 캐시 → 소유자 캐시 → 상태 사전을 거치는 중복을 제거**한 작업입니다. 이 단계에 남아 있던 일반 폰의 참가 사전 조회·빈 스냅샷 요청은 아래 11절의 후속 정리 대상입니다. [상세 작업·검증 기록][registered-visual-work]

### 재사용할 원칙

- **시작 사건에서 이미 아는 참가자를 지정합니다.** 회피·눕기·돌파·슬라이딩·떨치기는 발동한 자격자, 쳐내기는 해당 연출의 실제 상대, 제압은 성립한 운반 관계의 두 폰을 각 기능의 기존 경로에서 등록합니다. 일반 폰 전체를 대상으로 참가 여부를 새로 검증하지 않습니다. 무기 후보가 등록된 폰 참조를 소비하듯이 연출도 등록 자료를 소비합니다.
- **참가 표식에 필요한 참조와 분류를 함께 보관합니다.** 공용 몸 연출의 `BodyVisualEntry`는 `snapshotState`, `snapshotOwner`, `qualified`, `response`를 보관합니다. 자격·연출 시작/종료 사건에서 갱신하며, 렌더가 이를 다시 발견하지 않습니다. 비자격 쳐내기 참가자는 대응 연출만 읽고 본인 회피·눕기 등의 다섯 자세 플래그는 차단합니다.
- **등록 자료 읽기와 자격·상태 재탐색을 구분합니다.** 공통 렌더 입구에서 `BodyVisualFor`로 이미 게시된 자료 또는 미참가 결과를 얻는 일은 남습니다. 그 결과를 같은 렌더 문맥에 공유해 하위 패치가 반복 검색하지 않도록 합니다. 따라서 "일반 폰에 패치 호출이나 캐시 읽기가 전혀 없다"고 설명하지 않습니다. 이번 변경은 등록된 몸 연출 자료를 읽기 위해 자격·소유자·전투 상태를 다시 찾던 경로를 제거한 것입니다.
- **진행률을 고정하지 않습니다.** 렌더는 등록된 `state`를 기존 `statesLock` 안에서 읽어 스냅샷을 만듭니다. 같은 틱의 착지·기립·각도 변경도 반영하며, 이를 위해 별도 매 틱 게시자나 전체 폰 갱신 루프를 추가하지 않습니다. 같은 렌더 문맥에서는 얻은 스냅샷을 공유합니다. 값 스냅샷을 이미 게시하는 독립 특수 렌더는 기존 방식을 유지합니다.
- **수명 사건에서 갱신하고 연결의 유효성만 확인합니다.** 자격 변경은 이미 등록된 항목의 분류만 바꿉니다. 상태 제거·맵 이탈·로드 재구성·게임 교체에서 정리하거나 재등록합니다. 등록 스냅샷을 읽을 때 저장된 owner와 state의 연결 및 map 일치를 확인하되, 실패했다고 새로운 owner나 state를 찾아 연출 자료에 붙이지 않습니다. 공용 몸 연출 해제가 돌파·제압·슬라이딩/떨치기·기면서 쏴의 독립 기록을 함께 지우지 않도록 합니다.
- **기존 일반 무기 표시·준비 및 본체 조준 경로와 분리합니다.** 아직 참가 자료가 없는 경우의 기존 경로는 보존합니다. `RimKataGunReadyDrawUtility.Push`에도 등록된 공용 상태가 없는 폰의 기존 자격 캐시 확인이 남으며, 준비 무기 표시 후보는 기존 map component 경로를 사용할 수 있습니다. 이번 특수 연출 최적화를 전체 무기 렌더의 자격·상태 조회 제거로 확대 해석하지 않으며, 본체의 자격 판정·전투 진입 규칙 변경이나 존재 캐시 제거, 매 틱 강제 자격 재검사를 추가하지 않습니다.

### 현재 연결 위치

| 역할 | 파일·메서드 |
|---|---|
| 공용 몸 연출 등록, 이미 가진 state/owner 보관 | `RimKataCombatState.cs` — `RimKataResponseVisualParticipantCache.Refresh`, `RefreshBodyVisual` |
| 등록 참조로 현재 스냅샷 읽기 | 같은 파일 — `TryReadSnapshot`, `RimKataMapComponent.TryGetRegisteredVisualSnapshot` |
| 자격 변경과 게임 교체 연결 | `RimKataEligibilityCache.cs` — `SetQualified`, `ResetGame` → 참가 캐시의 `NotifyQualificationChanged`, `ResetGame` |
| 한 렌더 문맥에서 자료·미참가 결과 공유 | `RimKataVisualPatches.cs` — `RimKataWorldRenderContext.BodyFor`, `TrySnapshot`, `ReadSnapshot` |
| 이미 등록된 자격/owner를 후속 소비자에 전달 | 같은 파일 — `HasNextAim`, `Patch_PawnRenderer_RimKataDodgeOffset.Prefix`, `RimKataGunReadyDrawUtility.Push` |

검증은 해당 변경의 Release 빌드 경고 0·오류 0, diff 검사, 실제 DLL을 사용한 임시 검사 **39/39 통과**입니다. 상태 사전 없이 등록 참조만으로 읽기, 현재 진행률, 자격 변경, 비자격 대응 참가자의 자세 제한, 맵/owner 불일치 거부, 중첩/초상화 문맥 복원, 독립 기록 보존 및 게임 교체 정리를 확인했습니다. 인게임 렌더·전투·실측 성능은 확인하지 않았으므로 개선율을 제시하지 않습니다.

## 11. 2026-10-01 비참가 렌더의 사전 검색·빈 스냅샷 경로 정리

10절은 상태 재탐색을 줄였으나 모든 폰의 렌더 입구에서 참가 사전을 검색하고, 비참가로 확인한 뒤에도 빈 스냅샷 요청과 큰 Frame 준비를 반복했습니다. 사용자 프로파일 이미지와 실제 호출 흐름을 대조해 확인한 후 정리한 범위입니다.

- 실제 연출 등록·변경·해제 사건에서 `BodyVisualEntry`를 **폰 ID의 256칸 분할 페이지**에 게시합니다. 유효 ID를 가진 일반 폰의 `BodyVisualFor`는 사전을 검색하지 않고 해당 칸의 참조만 읽습니다. 비참가 폰의 조회는 페이지나 항목을 생성하지 않습니다. ID가 아직 없는 0·음수 대상에만 기존 사전 경로를 보존합니다.
- 참가자 사전은 사건에서 등록 항목을 열거·정리하는 용도로 남깁니다. 페이지·항목 참조는 `Volatile`로 공개하며, map 정리와 게임 교체도 기존 사건에서 함께 갱신합니다. 읽기와 삭제 모두 폰 참조를 비교하여 ID가 재사용된 다른 폰의 자료를 읽거나 지우지 않도록 합니다.
- 공용 렌더 문맥은 작은 값 구조체로 유지하며 참가 상태가 실제로 스냅샷을 요청할 때만 재사용 Frame을 가져옵니다. 비참가·초상화·특수 연출만 있는 폰은 공용 몸 스냅샷 Frame을 준비하지 않습니다. 중첩 일반 폰·초상화의 빈 문맥은 보존하여 부모의 연출 자료가 섞이지 않도록 합니다.
- 회피 위치·회전 캐시·넘어짐 회전의 세 입구는 등록 자료의 `HasSnapshot`을 먼저 보고, 비참가라면 `TryGetCachedActiveSnapshot → TrySnapshot → ReadSnapshot → TryReadSnapshot` 호출로 내려가지 않습니다. 다른 소비자가 직접 스냅샷을 요청해도 `TrySnapshot`에서 빈 자료를 거절합니다.
- **남는 비용을 구분합니다.** 바닐라의 폰별 패치 호출, 직접 슬롯 읽기, 중첩 문맥 보관·복원은 남습니다. 같은 렌더 호출 밖에서는 최신 게시 자료를 다시 읽으며, 스냅샷을 프레임 전체나 다음 틱까지 고정하지 않습니다. 실제 참가자의 상태 잠금·진행률 계산과 기존 일반 무기 준비·본체 조준 경로도 유지합니다. 실제 게임의 개선량은 재측정 전까지 확정하지 않습니다.
- 검증: Release 경고 0·오류 0, 실제 DLL 임시 fixture 71/71 통과. 비참가 Frame 미취득, 자격·진행률·중첩 문맥, ID 재사용과 맵·게임 해제, 병렬 게시·읽기를 확인했습니다. Harmony·Unity 렌더·인게임 성능은 별도 검증이 필요합니다. 메서드 색인은 122파일·2,688개 선언과 호출 관계로 갱신했으며 기존 흐름 추적 주의사항은 보존했습니다.

## 12. 2026-10-02 시체 렌더의 림카타 호출 제거

- 매 렌더 `Pawn.Dead` 검사와 시체 전용 Prefix/Finalizer·문맥 전달 방식은 폐기 상태를 유지합니다. 시체 문맥만 삭제하고 공통 폰 렌더 훅을 남긴 이전 단계도 요청을 충족하지 못했습니다. 함수 안에서 검사하고 반환하는 비용까지 시체 수에 비례했습니다.
- 바닐라는 `Corpse → InnerPawn → PawnRenderer` 경로를 공유합니다. DynamicDrawPhaseAt의 전역 회피·돌파 훅, GetDrawParms의 기면서 쏴 훅, 몸·머리·Carried의 전역 시작/종료 훅과 공통 장비 문맥 훅을 제거했습니다. 기존 보정 함수는 살아 있는 폰의 호출 경로에서만 실행합니다.
- 네이티브가 이미 만든 `PawnDrawParms.dead` 필드로 림카타 호출을 분기합니다. 시체는 몸·머리·장비의 기존 바닐라 출력으로 진행하며 Begin/BodyFor/BodyVisualFor·상태·자격·캐시 조회를 호출하지 않습니다. 추가 Pawn.Dead 조회, 시체 문맥, 사망자 목록, 사망 시 등록 해제는 없습니다. 네이티브 본문에 짧은 필드 분기는 남으므로 CPU 명령이 문자 그대로 0이라는 의미는 아닙니다.
- 살아 있는 다운 폰·기면서 쏴·제압·던지기·슬라이딩·떨치기·돌파·회피·초상화의 기존 렌더 수식과 중첩 문맥을 유지합니다. 사망 시 바닐라가 SetAllGraphicsDirty를 호출하여 몸 요청을 재생성합니다. 이를 위해 림카타 사망 훅을 추가하지 않습니다. 그림자 단독 출력은 원래 Standing 분기 안에서만 처리합니다.
- 장비는 RenderPawnAt의 캐시 출력과 Carried.PostDraw에서 살아 있는 폰만 전용 입구를 사용합니다. 전용 입구도 바닐라 장비 함수를 호출하므로 그 함수에 붙은 외부 모드 패치는 실행됩니다. 다만 외부 모드가 이 두 네이티브 경로 밖에서 바닐라 장비 함수를 직접 호출하면 림카타 장비 문맥을 새로 만들지 않습니다.
- 검증: Release 경고 0·오류 0, diff 검사 통과. 임시 검사 준비 19·장비 11·몸/머리 46조건 통과. 실제 게임 메서드의 패치 생성·두 장비 패치 순서, 시체 입력의 림카타 호출 0회, 살아 있는 입력의 호출 및 중첩 finally·예외 전달을 확인했습니다. 실행 검사는 Unity 본문을 작은 대역으로 대체했으며 Draw 전체 네이티브 패치 실행은 외부 .NET의 Unity ECall 제약 때문에 검증하지 못했습니다. 인게임 렌더·실측 성능은 미검증입니다.

## 13. 2026-10-01 슬라이딩 중간 공격 좌표

- 슬라이딩은 도착 셀을 실제 점유하므로 일반 Pawn 위치로 발사하면 도착점에서 총알이 나옵니다. NativeAttack의 기존 실행 시작·종료에 전용 값 문맥만 연결하고, 슬라이딩 요청이 가진 출발 좌표·도착 셀·현재 틱으로 공격 좌표를 계산합니다. 12틱 이동의 6틱에는 중간점이며 몸 렌더도 같은 계산을 사용합니다. Pawn.Position이나 추격 대상 정보는 바꾸지 않습니다.
- `TryGetShotCenter`는 해당 실행 문맥이 일치할 때 슬라이딩 좌표를 우선 반환합니다. 기존 바닐라 Projectile.Launch, CE 궤도 보정과 Muzzle Flash 접점을 이용하며, 바닐라 ShotFlash는 셀 기반 위치 대신 소수점 좌표로 표시합니다. 일반 공격·떨치기·기립은 전용 문맥을 만들지 않습니다.
- 바닐라·CE 근접 피해 반복자는 기존 SetAngle 호출만 보정합니다. 명중·피해량·관통력·피해 부위 선택과 피해 전달은 원래 경로 그대로이며, 피해를 재계산하거나 새 피해 호출·반환 대기·추가 피해 열거를 하지 않습니다.
- 소비자는 공격 실행에서 직접 등록한 Verb·공격자만 대조합니다. 새 폰 순회·자격 검사·상태 사전 검색·매 틱 탐색을 추가하지 않습니다. 중첩 다른 공격은 부모 좌표를 가리고 종료 때 복원하며 CE 조준 연기 분기 전에도 문맥을 해제합니다.
- 검증: Release 경고 0·오류 0, 임시 DLL 검사 33/33 통과. 좌표 보간·쌍수/중첩 범위·종료, 기존 Launch Prefix와 Muzzle Flash 좌표 전달, 바닐라/CE 피해 반복자와 ShotFlash의 실제 Harmony 패치 생성까지 확인했습니다. Unity의 방향 변환·섬광 표시·인게임 전투는 미검증입니다.

## 14. 2026-10-02 회피·슬라이딩·떨치기의 작업 참가자 직접 소비

- DriverTick의 공통 Prefix가 모든 작업 폰에 자격 캐시를 먼저 조회하던 구조를 제거했습니다. PatherTick의 FullBodyBusy 예외, StartJob, 경로 도착·실패도 같은 원칙으로 정리했습니다. 참가자 검사와 자격 검사 순서를 바꾸는 방식은 일반 폰의 조회 비용을 다른 캐시로 옮기므로 사용하지 않습니다.
- 동작을 시작할 때 이미 확보한 combat/반응 상태와 담당 Job 참조를 전용 슬롯에 발행합니다. 공통 메서드에는 참가 수·Pawn ID 슬롯·Pawn 참조 분기만 삽입하여 비참가자에게 림카타 함수를 호출하지 않습니다. 참가자도 자격·BodyVisualFor·맵 컴포넌트·상태 사전을 재탐색하지 않고 전달받은 상태로 작업 차단을 판단합니다. 참가자가 있을 때 비참가자에 대한 직접 슬롯 읽기는 남습니다.
- 회피 이동의 활성 RimKataAttack 작업 예외와 FullBodyBusy 예외를 보존합니다. 슬라이딩·떨치기만 원래 작업을 막고, 기립이나 마지막 공격 대기만 남으면 작업 틱을 막지 않습니다. 기립 참가자는 새 작업으로 교체될 때 취소되도록 실제 종료까지 등록을 유지합니다.
- 실제 시작, 회피 착지·실패·전환, 반응 종료, 자격 상실, 상태·맵 제거, 게임 교체, 불러오기 복원에 등록·해제를 연결했습니다. 상태 참조가 일치할 때만 슬롯을 지워 이전 정리가 새 참가자를 지우지 않습니다. 작업 소비 경로에 건강·다운·사망·자격 재검증을 추가하지 않습니다. 기존 피해·공격 유효성 및 실제 작업 취소는 그대로입니다.
- 기존 GetStatus→GetDodgeMovementStatus의 반복 맵/상태 조회는 소비자가 없어져 삭제했습니다. 화면의 ConditionalWeakTable<Pawn, Entry>는 다른 캐시에도 같은 이름이 있으므로 이번 변경으로 218회 전체가 사라진다고 단정하지 않습니다.
- 검증: Release 경고·오류 0, diff 검사 및 임시 검사 168조건 통과. 실제 다섯 네이티브 메서드의 Harmony 패치 생성, 비참가자의 림카타 helper/자격/연출/맵 상태 조회 0회, 회피 작업 예외·반응 단계·담당 Job 교체·ID 재사용·등록/해제·게임 초기화를 확인했습니다. 실행 검사에서는 원래 게임 작업 본문을 작은 카운터 본문으로 대체했습니다. 인게임 이동·전투·실측 성능은 미검증입니다.

## 15. 2026-10-02 장비·전투 표시기·자동 전투 틱의 일반 폰 진입 제거

- 공통 장비 호출에 붙어 있던 돌파·슬라이딩·제압 Prefix는 특수 장비 입구로 제한했습니다. 돌파·반응 동작 본인과 제압 양측은 기존 사건 기반 임시 등록을 소비합니다. 관련 없는 일반 폰은 바닐라 장비 출력으로 진행합니다.
- 평시 쌍수·일반 조준은 몸 연출이 없어도 필요한 자격자의 기능입니다. 기존 등록 슬롯에 별도의 registeredQualified 값을 자격 획득·상실 사건에서 발행합니다. 몸 연출 종료가 이 값을 지우지 않으며, 쳐내기·제압 상대의 임시 등록이 전투 자격을 부여하지 않습니다. 일반 폰 명단이나 주기적인 자격 재검사를 추가하지 않습니다.
- 장비 진입점은 바닐라의 기존 dead 값으로 시체를 먼저 제외하고, 살아 있는 폰의 ID 슬롯·Pawn 참조를 직접 분기합니다. 등록 자료를 장비 문맥으로 전달하므로 Begin/BodyVisualFor로 다시 찾지 않습니다. GunReady의 자료 없음 → IsCachedQualifiedPawn fallback은 삭제했습니다. 특수 연출 중 다른 폰을 중첩 출력할 때는 부모 연출을 가리는 빈 문맥만 열며, 종료·예외 시 부모를 복원합니다.
- CombatIndicators와 DraftedRimKataFire의 공통 Postfix를 제거했습니다. 네이티브 본문 정상 반환 뒤 등록 자격자만 기존 표시기·TickCombat을 호출합니다. 비자격 연출 참가자는 전투 틱에 들어가지 않습니다. 표시기의 단독·단체 선택 의미와 전투 본체의 공격 방식·검증은 변경하지 않습니다.
- 직접 슬롯 읽기 자체는 남으며, 이를 전체 연산 0으로 표현하지 않습니다. 일반 폰의 자격·상태 존재·맵 조회와 해당 림카타 소비 함수 호출을 제거한 것입니다. 다른 모드가 네이티브 본문 전체를 생략하는 Prefix를 쓰면 본문 끝의 표시기·전투 틱도 실행되지 않습니다.
- 검증: Release 경고·오류 0, diff 검사 통과. 임시 RegisteredEntryChecks의 174조건 통과. 실제 네이티브 4메서드 Harmony 생성, 등록 8종·ID 재사용·자격/연출 독립 해제·맵/게임 초기화, 일반 폰 소비 호출 0 및 원래 장비 출력 유지, 실제 특수 문맥의 중첩·예외 복원과 자격 fallback 제거를 확인했습니다. Unity 그리기와 전투 본문은 임시 대역으로 분리해 검사했으므로 실제 화면·전투 및 성능은 게임에서 확인해야 합니다.

## 16. 2026-10-02 돌파 문 장애물 처리의 일반 폰 진입 제거

- `BuildingBlockingNextPathCell`은 일반 이동의 진행률 갱신과 다음 셀 진입에서도 호출됩니다. 여기에 붙었던 `Patch_PawnPathFollower_RimKataBreachDoorBlock.Postfix`는 돌파를 쓰지 않는 폰에게도 실행됐습니다.
- 공통 Postfix를 제거하고 바닐라 반환부에서 결과가 `Building_Door`인 경우에만 현재 JobDriver를 직접 확인합니다. `JobDriver_RimKataBreach`인 경우에만 돌파 전용 처리를 호출하며, 일반 폰·돌파하지 않는 자격자는 돌파 helper와 상태 조회에 진입하지 않습니다. null/비문 결과는 폰·작업 필드도 읽지 않습니다.
- 지정한 문, 소유 작업 ID, 미파괴 상태, Run/Slide 조건을 보존합니다. 실제 문 파괴는 기존 다음 셀 진입 Prefix와 `BreakDoor`가 담당합니다. 새 자격 조회·렌더 참가자 조회·캐시는 추가하지 않습니다. 문 결과의 작업 종류 직접 분기는 남습니다.
- 다른 모드가 원본을 Prefix에서 생략하면 이 반환부도 생략되며, 다른 Postfix가 나중에 바꾼 결과를 재검사하지 않습니다. 이 차이를 덮으려고 일반 폰 공통 Postfix를 다시 추가하지 않습니다.
- Release 경고·오류 0, 임시 `RimKata-BreachDoorGateChecks-20261002` 검사 132개 통과. 실제 게임 메서드의 Harmony 패치 생성과 두 반환 경로, 일반/자격/돌파 폰의 helper·상태 조회 횟수, 단계·문·작업 ID·미파괴 조건을 확인했습니다. 지도 본문만 대역으로 바꾸고 실제 DoorState/Get을 사용했으며 인게임 이동·문 파괴·성능은 별도 확인 대상입니다.

## 17. 2026-10-02 떨치기 공격 시점의 인접 셀 직접 선택

- 떨치기의 `EnemyAt`이 일반 후보 등록에 성공해야 대상을 선택하던 연결을 제거했습니다. 등록 함수에 `randomAttackVerified=true`를 넘겨도 하위 슬롯 반경 계산에서 무작위 공격 설정을 다시 검사하여 새 방향의 적이 탈락했습니다. 이제 떨치기만 현재 셀의 유효한 적대 폰을 직접 사용합니다. 슬라이딩의 기존 후보 등록과 일반 공격 본체는 유지합니다.
- 전 틱의 컴포넌트 예약 루프를 제거했습니다. 기존 `MotionJobGate`가 통과시킨 떨치기 참가자만 작업 틱에서 3·6·…·24틱의 해당 방향 셀을 확인하고, 뒤따르는 같은 틱 장비 `VerbTick`에 공격을 예약합니다. 0틱 원래 공격, 쌍수 반대 방향, 24틱 원래 대상 우선은 유지합니다. 없는 무기 슬롯은 셀을 조사하지 않으며, 빈 셀은 공격하지 않습니다.
- 특수 공격 예약은 일반 쿨다운을 읽어 대기하지 않습니다. 실제 완료된 공격이 회복 시간을 갱신하므로 마지막으로 발동한 공격부터의 쿨다운으로 본체에 복귀합니다. 직접 피해 계산·WarmupComplete 동기 호출·일반 폰 신규 검사·주변 8셀 일괄 사전 탐색을 추가하지 않습니다.
- 근접 사격 비활성 시 슬라이딩·떨치기는 해당 무기 안의 근접 verb를 기존 가중 선택기로 고릅니다. 둘 다 touch 불가인 대상을 총알로 대신 공격하지 않습니다. 2026-10-02 후속 수정으로 슬라이딩의 먼 대상 사격 fallback도 제거했습니다. 최종 근접 verb는 탄약을 요구하지 않고, 근접 사격이 활성일 때의 사격 verb는 기존 CE 탄약 검사를 유지합니다.
- Release 경고·오류 0, 스케줄/대상 선택/기존 참가자 진입 105검사 및 실제 ReactiveAttack.Queue 분기 41검사 통과. 테스트는 지도·적대/상태 판정·네이티브 발사 등 일부를 대역으로 사용했으며 인게임 피해·연출·실측 성능은 미검증입니다.
- 슬라이딩 후속 수정은 별도 `RimKata-SlidingCloseFireChecks-20261002` 80검사로 확인했습니다. 주·부 슬롯, 설정 on/off, touch 여부, 탄약, 복구 단계와 실행 전 거리 이탈을 검사했고 actual Queue/CanContinue 외의 게임 환경·무기 선택·발사는 일부 대역입니다. 일반 폰 검사나 새 캐시 없이 기존 특수 공격 선택 조건만 변경했습니다.

## 18. 2026-10-02 넘어진 동안 오사 방지의 피격 분기

- 새 상시 틱·렌더 조회·일반 폰 자격 검사를 추가하지 않습니다. 기존 원거리 피격 처리에서만 사건으로 등록된 GroundPose의 Active 상태를 읽고, 넘어짐 상태이면서 발사자가 비적대인 경우에만 기존 방어 자격과 설정 확률을 확인합니다.
- 넘어지는 중·넘어진 상태(구르기 포함)·그 상태에서 기립하는 중을 포함합니다. 엎드려 사격·그 자세에서의 기립·단순 다운·서 있는 돌파 대기는 이 설정의 대상이 아닙니다.
- 기본 50%이며 원거리 회피 활성 플래그와 독립입니다. 성공 시 피해 흡수와 해당 탄환 stagger 억제만 처리하고 회피 연출·이동·피격 작업 통지는 호출하지 않습니다. 실패하면 이 분기에서 피해를 허용하며 추가 원거리 회피를 굴리지 않습니다.
- 투사체 프레임 및 투사체 없는 CloseShot의 기존 결과 저장을 사용해 같은 공격의 추가 피해에서 성공·실패를 다시 굴리지 않습니다. 공격자 본인·발사자 없음·적대 발사자·폭발은 새 오사 방지의 대상이 아니며 기존 처리 경계를 유지합니다.

## 19. 2026-10-02 선택 기즈모의 부무기 중복 조회 제거

- RangedWeapon.Postfix에서 선택 목록을 매번 순회하던 처리를 기존 GetSelectedAttackGizmoFacts에 합쳤습니다. 동일 프레임·GUI 이벤트·선택 키에서는 원거리 부무기 존재를 포함한 결과를 재사용합니다. 폰 5명마다 전체 5명을 다시 확인하는 중첩 순회를 추가하지 않습니다.
- 부무기는 자격 확인 후 SecondaryWeaponWithVerifiedAccess로 등록 참조를 먼저 읽습니다. true + null은 확정된 부무기 없음이므로 Registry를 찾지 않습니다. 등록 정보가 없는 제한 해제 자격자 등의 기존 fallback은 유지합니다. 같은 틱 무기 캐시 전에 Game.GetComponent를 반복 호출하는 경로로 되돌리지 않습니다.
- 통합 공격 명령의 부 슬롯 사용 가능 조건과 원거리 부무기 존재 조건을 독립적으로 계산해 기존 표시 결과를 유지합니다. 근접 부무기를 찾았더라도 아직 원거리 존재 여부를 모르면 뒤의 선택 폰을 계속 확인합니다.
- 비소집 근접 기즈모는 자격·공격 가능 여부 계산 전에 바닐라로 보냅니다. 비소집 부무기 공격·교체·돌진 메뉴와 일반 전투·부무기 등록 생명주기는 변경하지 않습니다.
- 장비·자격·프로필 변화는 기존 등록 참조 갱신을 사용하고 다음 GUI 이벤트/프레임에 새 선택 결과를 만듭니다. 이번 작업은 기존 이벤트 범위 캐시를 확장한 것으로, 같은 이벤트 내부의 목록 중간 항목 교체까지 감지하는 새 영구 캐시나 전역 이벤트 패치를 추가한 것은 아닙니다.

## 원문 연결

각 링크는 원문 구간의 시작 줄로 이동합니다. 이전 방식의 설명과 후속 폐기 기록이 함께 있는 경우 후속 결정을 우선합니다.

[native]: <C:/Users/user/Documents/RimworldModsFolder/RimKata/RimKata_code_cleanup_worklog_260830.md:77>
[close-native]: <C:/Users/user/Documents/RimworldModsFolder/RimKata/RimKata_code_cleanup_worklog_260830.md:144>
[ordinary]: <C:/Users/user/Documents/RimworldModsFolder/RimKata/RimKata_code_cleanup_worklog_260830.md:294>
[participant]: <C:/Users/user/Documents/RimworldModsFolder/RimKata/RimKata_code_cleanup_worklog_260830.md:3076>
[registered-visual-work]: <C:/Users/user/Documents/RimworldModsFolder/RimKata/RimKata_code_cleanup_worklog_260830.md:3198>
[presence]: <C:/Users/user/Documents/RimworldModsFolder/RimKata/RimKata_code_cleanup_worklog_260830.md:2727>
[binding]: <C:/Users/user/Documents/RimworldModsFolder/RimKata/RimKata_code_cleanup_worklog_260830.md:283>
[candidate-pass]: <C:/Users/user/Documents/RimworldModsFolder/RimKata/RimKata_code_cleanup_worklog_260830.md:163>
[wait-search]: <C:/Users/user/Documents/RimworldModsFolder/RimKata/RimKata_code_cleanup_worklog_260830.md:3066>
[candidate-trust]: <C:/Users/user/Documents/RimworldModsFolder/RimKata/RimKata_code_cleanup_worklog_260830.md:413>
[ring-concurrent]: <C:/Users/user/Documents/RimworldModsFolder/RimKata/RimKata_code_cleanup_worklog_260830.md:38>
[render-readonly]: <C:/Users/user/Documents/RimworldModsFolder/RimKata/RimKata_code_cleanup_worklog_260830.md:467>
[prepared14]: <C:/Users/user/Documents/RimworldModsFolder/RimKata/RimKata_code_cleanup_worklog_260830.md:2603>
[door]: <C:/Users/user/Documents/RimworldModsFolder/RimKata/RimKata_code_cleanup_worklog_260830.md:2930>
[occupancy]: <C:/Users/user/Documents/RimworldModsFolder/RimKata/RimKata_code_cleanup_worklog_260830.md:104>
[neutral]: <C:/Users/user/Documents/RimworldModsFolder/RimKata/RimKata_code_cleanup_worklog_260830.md:23>
[job-state]: <C:/Users/user/Documents/RimworldModsFolder/RimKata/RimKata_code_cleanup_worklog_260830.md:2202>
[controller]: <C:/Users/user/Documents/RimworldModsFolder/RimKata/RimKata_code_cleanup_worklog_260830.md:432>
[continuity6]: <C:/Users/user/Documents/RimworldModsFolder/RimKata/RimKata_code_cleanup_worklog_260830.md:450>
[mobile]: <C:/Users/user/Documents/RimworldModsFolder/RimKata/RimKata_code_cleanup_worklog_260830.md:424>
[loadout]: <C:/Users/user/Documents/RimworldModsFolder/RimKata/RimKata_code_cleanup_worklog_260830.md:272>
[close-entry]: <C:/Users/user/Documents/RimworldModsFolder/RimKata/RimKata_code_cleanup_worklog_260830.md:259>
[response-tick]: <C:/Users/user/Documents/RimworldModsFolder/RimKata/RimKata_code_cleanup_worklog_260830.md:309>
[defense-reuse]: <C:/Users/user/Documents/RimworldModsFolder/RimKata/RimKata_code_cleanup_worklog_260830.md:301>
[continuity10]: <C:/Users/user/Documents/RimworldModsFolder/RimKata/RimKata_code_cleanup_worklog_260830.md:18>
[command-aim]: <C:/Users/user/Documents/RimworldModsFolder/RimKata/RimKata_code_cleanup_worklog_260830.md:2824>
[idle-candidates]: <C:/Users/user/Documents/RimworldModsFolder/RimKata/RimKata_code_cleanup_worklog_260830.md:2890>
[breach-wait]: <C:/Users/user/Documents/RimworldModsFolder/RimKata/RimKata_code_cleanup_worklog_260830.md:3056>
[explosive-empty]: <C:/Users/user/Documents/RimworldModsFolder/RimKata/RimKata_code_cleanup_worklog_260830.md:1897>
[move-gate5]: <C:/Users/user/Documents/RimworldModsFolder/RimKata/RimKata_code_cleanup_worklog_260830.md:2392>
[move-gate7]: <C:/Users/user/Documents/RimworldModsFolder/RimKata/RimKata_code_cleanup_worklog_260830.md:403>
[moving-batch]: <C:/Users/user/Documents/RimworldModsFolder/RimKata/RimKata_code_cleanup_worklog_260830.md:2539>
[movement-watch]: <C:/Users/user/Documents/RimworldModsFolder/RimKata/RimKata_code_cleanup_worklog_260830.md:362>
[ring-maintenance]: <C:/Users/user/Documents/RimworldModsFolder/RimKata/RimKata_code_cleanup_worklog_260830.md:332>
[target-move]: <C:/Users/user/Documents/RimworldModsFolder/RimKata/RimKata_code_cleanup_worklog_260830.md:340>
[prepared-pass]: <C:/Users/user/Documents/RimworldModsFolder/RimKata/RimKata_code_cleanup_worklog_260830.md:346>
[geometry]: <C:/Users/user/Documents/RimworldModsFolder/RimKata/RimKata_code_cleanup_worklog_260830.md:324>
[incapacitated]: <C:/Users/user/Documents/RimworldModsFolder/RimKata/RimKata_code_cleanup_worklog_260830.md:31>
[hud]: <C:/Users/user/Documents/RimworldModsFolder/RimKata/RimKata_code_cleanup_worklog_260830.md:1749>
[weather]: <C:/Users/user/Documents/RimworldModsFolder/RimKata/RimKata_code_cleanup_worklog_260830.md:1782>
[idle-intercept]: <C:/Users/user/Documents/RimworldModsFolder/RimKata/RimKata_code_cleanup_worklog_260830.md:1826>
[dormancy]: <C:/Users/user/Documents/RimworldModsFolder/RimKata/RimKata_code_cleanup_worklog_260830.md:2307>
[projectile-recipients]: <C:/Users/user/Documents/RimworldModsFolder/RimKata/RimKata_code_cleanup_worklog_260830.md:2528>
[explosive-scheduler]: <C:/Users/user/Documents/RimworldModsFolder/RimKata/RimKata_code_cleanup_worklog_260830.md:2671>
[bullet-defense]: <C:/Users/user/Documents/RimworldModsFolder/RimKata/RimKata_code_cleanup_worklog_260830.md:2772>
[idle-ai]: <C:/Users/user/Documents/RimworldModsFolder/RimKata/RimKata_code_cleanup_worklog_260830.md:2864>
[ground-events]: <C:/Users/user/Documents/RimworldModsFolder/RimKata/RimKata_code_cleanup_worklog_260830.md:2918>
[crawl-events]: <C:/Users/user/Documents/RimworldModsFolder/RimKata/RimKata_code_cleanup_worklog_260830.md:2926>
[hunting-conceal]: <C:/Users/user/Documents/RimworldModsFolder/RimKata/RimKata_code_cleanup_worklog_260830.md:2904>
[breach-access]: <C:/Users/user/Documents/RimworldModsFolder/RimKata/RimKata_code_cleanup_worklog_260830.md:3004>
[mine-event]: <C:/Users/user/Documents/RimworldModsFolder/RimKata/RimKata_code_cleanup_worklog_260830.md:3110>
[subdue]: <C:/Users/user/Documents/RimworldModsFolder/RimKata/RimKata_code_cleanup_worklog_260830.md:3146>
[gunready5]: <C:/Users/user/Documents/RimworldModsFolder/RimKata/RimKata_code_cleanup_worklog_260830.md:2320>
[range-reuse]: <C:/Users/user/Documents/RimworldModsFolder/RimKata/RimKata_code_cleanup_worklog_260830.md:2332>
[indicators]: <C:/Users/user/Documents/RimworldModsFolder/RimKata/RimKata_code_cleanup_worklog_260830.md:2381>
[dodge-render]: <C:/Users/user/Documents/RimworldModsFolder/RimKata/RimKata_code_cleanup_worklog_260830.md:2423>
[render-owner]: <C:/Users/user/Documents/RimworldModsFolder/RimKata/RimKata_code_cleanup_worklog_260830.md:373>
[gunready-copy]: <C:/Users/user/Documents/RimworldModsFolder/RimKata/RimKata_code_cleanup_worklog_260830.md:2445>
[carry-copy]: <C:/Users/user/Documents/RimworldModsFolder/RimKata/RimKata_code_cleanup_worklog_260830.md:2456>
[gizmo]: <C:/Users/user/Documents/RimworldModsFolder/RimKata/RimKata_code_cleanup_worklog_260830.md:2468>
[secondary-tick]: <C:/Users/user/Documents/RimworldModsFolder/RimKata/RimKata_code_cleanup_worklog_260830.md:2478>
[render-pairrange]: <C:/Users/user/Documents/RimworldModsFolder/RimKata/RimKata_code_cleanup_worklog_260830.md:2488>
[colonist-icon]: <C:/Users/user/Documents/RimworldModsFolder/RimKata/RimKata_code_cleanup_worklog_260830.md:65>
[smooth-start]: <C:/Users/user/Documents/RimworldModsFolder/RimKata/RimKata_code_cleanup_worklog_260830.md:2898>
[smooth24]: <C:/Users/user/Documents/RimworldModsFolder/RimKata/RimKata_code_cleanup_worklog_260830.md:3134>
[ground-indicator]: <C:/Users/user/Documents/RimworldModsFolder/RimKata/RimKata_code_cleanup_worklog_260830.md:2902>
[breach-render]: <C:/Users/user/Documents/RimworldModsFolder/RimKata/RimKata_code_cleanup_worklog_260830.md:3022>
[map-cleanup]: <C:/Users/user/Documents/RimworldModsFolder/RimKata/RimKata_code_cleanup_worklog_260830.md:3096>
[opening-bind]: <C:/Users/user/Documents/RimworldModsFolder/RimKata/RimKata_code_cleanup_worklog_260830.md:56>
[prepared-cleanup]: <C:/Users/user/Documents/RimworldModsFolder/RimKata/RimKata_code_cleanup_worklog_260830.md:2617>
[catalog]: <C:/Users/user/Documents/RimworldModsFolder/RimKata/RimKata_code_cleanup_worklog_260830.md:2631>
[door-environment]: <C:/Users/user/Documents/RimworldModsFolder/RimKata/RimKata_code_cleanup_worklog_260830.md:2940>
[door-recipe]: <C:/Users/user/Documents/RimworldModsFolder/RimKata/RimKata_code_cleanup_worklog_260830.md:3036>
[door-cleanup]: <C:/Users/user/Documents/RimworldModsFolder/RimKata/RimKata_code_cleanup_worklog_260830.md:3122>
[pending-pool]: <C:/Users/user/Documents/RimworldModsFolder/RimKata/RimKata_code_cleanup_worklog_260830.md:317>
[first-move]: <C:/Users/user/Documents/RimworldModsFolder/RimKata/RimKata_code_cleanup_worklog_260830.md:355>
[damage-measure]: <C:/Users/user/Documents/RimworldModsFolder/RimKata/RimKata_code_cleanup_worklog_260830.md:231>
[death-stack]: <C:/Users/user/Documents/RimworldModsFolder/RimKata/RimKata_code_cleanup_worklog_260830.md:130>
[occupancy-measure]: <C:/Users/user/Documents/RimworldModsFolder/RimKata/RimKata_code_cleanup_worklog_260830.md:113>
[empty-selection]: <C:/Users/user/Documents/RimworldModsFolder/RimKata/RimKata_code_cleanup_worklog_260830.md:194>
[intercept-prediction]: <C:/Users/user/Documents/RimworldModsFolder/RimKata/RimKata_code_cleanup_worklog_260830.md:1863>
[temporary-request]: <C:/Users/user/Documents/RimworldModsFolder/RimKata/RimKata_code_cleanup_worklog_260830.md:1874>
[ai-projectile]: <C:/Users/user/Documents/RimworldModsFolder/RimKata/RimKata_code_cleanup_worklog_260830.md:1907>
[drafted-reuse]: <C:/Users/user/Documents/RimworldModsFolder/RimKata/RimKata_code_cleanup_worklog_260830.md:2369>
[inactivity-gate]: <C:/Users/user/Documents/RimworldModsFolder/RimKata/RimKata_code_cleanup_worklog_260830.md:2499>
[compat-defense]: <C:/Users/user/Documents/RimworldModsFolder/RimKata/RimKata_code_cleanup_worklog_260830.md:2912>
[compat-pocket]: <C:/Users/user/Documents/RimworldModsFolder/RimKata/RimKata_code_cleanup_worklog_260830.md:2910>
[melee-gizmo]: <C:/Users/user/Documents/RimworldModsFolder/RimKata/RimKata_code_cleanup_worklog_260830.md:2413>
[combat-icon]: <C:/Users/user/Documents/RimworldModsFolder/RimKata/RimKata_code_cleanup_worklog_260830.md:2561>
