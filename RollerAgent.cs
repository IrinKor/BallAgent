using UnityEngine;
using Unity.MLAgents;
using Unity.MLAgents.Sensors;
using Unity.MLAgents.Actuators;

public class RollerAgent : Agent
{
    [Header("Movement Settings")]
    public float moveSpeed = 10f;

    [Header("Observation Settings")]
    public bool observeRedCubes = true;
    public bool useSimplifiedObservations = false; // Упрощённые наблюдения (меньше входов)

    [Header("Action Smoothing")]
    [Range(0f, 0.95f)]
    public float actionSmoothing = 0.7f; // Сглаживание действий (low-pass filter)

    [Header("Reward Settings")]
    public float stepPenalty = -0.01f;           // Штраф за каждый шаг
    public float idlePenalty = -0.02f;           // Штраф за бездействие
    public float idleThreshold = 0.2f;            // Порог скорости для бездействия
    public int idleFramesRequired = 30;           // Сколько кадров бездействия нужно для штрафа

    public float greenCubeBaseReward = 50f;       // Базовая награда за зелёный куб
    public float greenCubeProgressiveBonus = 20f; // Прогрессивный бонус (50, 70, 90, 110)
    public float redCubePenalty = -15f;           // Штраф за красный куб
    public float fallPenalty = -5f;               // Штраф за падение
    public float targetReward = 100f;             // Награда за финиш

    public float shapingGain = 0.05f;             // Коэффициент reward shaping'а
    public float shapingMinDelta = 0.01f;         // Минимальное изменение дистанции для shaping'а

    // Приватные поля
    private Rigidbody rb;
    private Vector3 startPos;
    private GameManager gameManager;
    private Transform target;

    // Для reward shaping и отслеживания цели
    private float prevDistanceToGoal;
    private Vector3 lastGoalPosition;             // Отслеживание смены цели
    private int stepCount;

    // Для защиты от бездействия
    private float lastVelocityMagnitude;
    private int idleFramesCount;

    // Для сглаживания действий
    private Vector3 filteredAction;
    private bool isFilterInitialized = false;

    // Safeguard: защита от двойного сбора одного куба
    private Collider lastCollectedGreen;

    // Связи с GameManager
    public void SetGameManager(GameManager m) => gameManager = m;
    public void SetTarget(Transform t) => target = t;

    // Метод, вызываемый GameManager при сборе зелёного куба (дополнительная логика)
    public void OnGreenCubeCollected()
    {
        // Здесь можно добавить дополнительную логику при сборе зелёного куба,
        // например, визуальные эффекты или звук.
        Debug.Log($"[RollerAgent] OnGreenCubeCollected вызван GameManager'ом");
    }

    public override void Initialize()
    {
        rb = GetComponent<Rigidbody>();
        startPos = transform.position;
        if (gameManager == null) gameManager = FindAnyObjectByType<GameManager>();
        filteredAction = Vector3.zero;
        Debug.Log($"[RollerAgent] Initialize — startPos: {startPos}, moveSpeed: {moveSpeed}, " +
                  $"observeRedCubes: {observeRedCubes}, useSimplifiedObservations: {useSimplifiedObservations}");
    }

    public override void OnEpisodeBegin()
    {
        stepCount = 0;
        idleFramesCount = 0;
        lastVelocityMagnitude = 0f;
        lastCollectedGreen = null;
        isFilterInitialized = false;
        filteredAction = Vector3.zero;

        // Случайная начальная позиция в небольшой области
        transform.position = startPos + new Vector3(Random.Range(-1f, 1f), 0, Random.Range(-1f, 1f));
        transform.rotation = Quaternion.identity;
        rb.linearVelocity = Vector3.zero;
        rb.angularVelocity = Vector3.zero;
        gameManager?.OnEpisodeStart();

        // Инициализация reward shaping
        Vector3 goal = gameManager != null ? gameManager.GetNextCrumbPosition() : transform.position;
        lastGoalPosition = goal;
        prevDistanceToGoal = Vector3.Distance(transform.position, goal);

        Debug.Log($"[RollerAgent] OnEpisodeBegin — позиция: {transform.position}, цель: {goal}, дистанция: {prevDistanceToGoal:F2}");
    }

    public override void CollectObservations(VectorSensor sensor)
    {
        // Safeguard: проверка на NaN
        Vector3 myPos = IsValidVector(transform.localPosition) ? transform.localPosition : Vector3.zero;
        Vector3 targetPos = (target != null && IsValidVector(target.localPosition)) ? target.localPosition : Vector3.zero;
        Vector3 vel = IsValidVector(rb.linearVelocity) ? rb.linearVelocity : Vector3.zero;

        if (useSimplifiedObservations)
        {
            // === Упрощённые наблюдения (9 параметров) ===
            // Относительное положение до цели (нормализованное)
            Vector3 toNext = gameManager != null ? gameManager.GetNextCrumbPosition() - transform.position : Vector3.zero;
            toNext = Vector3.ClampMagnitude(toNext, 20f);
            sensor.AddObservation(toNext.normalized);     // 3
            sensor.AddObservation(toNext.magnitude / 20f); // 1

            // Скорость
            sensor.AddObservation(vel.x / 10f);           // 1
            sensor.AddObservation(vel.z / 10f);           // 1

            // Направление до второго куска (для планирования)
            Vector3 toSecond = gameManager != null ? gameManager.GetSecondCrumbPosition() - transform.position : Vector3.zero;
            toSecond = Vector3.ClampMagnitude(toSecond, 20f);
            sensor.AddObservation(toSecond.normalized);    // 3

            // Итого: 3+1+1+1+3 = 9
        }
        else
        {
            // === Полные наблюдения (17 или 26) ===
            sensor.AddObservation(myPos);                  // 3
            sensor.AddObservation(targetPos);              // 3
            sensor.AddObservation(vel.x);                  // 1
            sensor.AddObservation(vel.z);                  // 1
            sensor.AddObservation(vel.magnitude);          // 1
            sensor.AddObservation(GetSafeDirectionToNext()); // 3
            sensor.AddObservation(GetSafeDirectionToSecond()); // 3
            sensor.AddObservation(gameManager != null ? Mathf.Clamp01(gameManager.GetProgress()) : 0f); // 1
            sensor.AddObservation(gameManager != null && gameManager.IsTargetReachable ? 1f : 0f); // 1
                                                                                                   // Итого: 3+3+1+1+1+3+3+1+1 = 17

            // Красные кубы — только если включены
            if (observeRedCubes)
            {
                Vector3[] redPositions = gameManager != null ? gameManager.GetNearbyRedCubePositions(3) : new Vector3[3];
                for (int i = 0; i < 3; i++)
                {
                    Vector3 safePos = IsValidVector(redPositions[i]) ? redPositions[i] : Vector3.zero;
                    sensor.AddObservation(safePos);        // 3*3 = 9
                }
                // Итого с красными: 26
            }
        }
    }

    // Вспомогательные методы для безопасного получения направлений
    private Vector3 GetSafeDirectionToNext()
    {
        if (gameManager == null) return Vector3.zero;
        Vector3 toNext = gameManager.GetNextCrumbPosition() - transform.position;
        return IsValidVector(toNext) ? Vector3.ClampMagnitude(toNext, 20f) : Vector3.zero;
    }

    private Vector3 GetSafeDirectionToSecond()
    {
        if (gameManager == null) return Vector3.zero;
        Vector3 toSecond = gameManager.GetSecondCrumbPosition() - transform.position;
        return IsValidVector(toSecond) ? Vector3.ClampMagnitude(toSecond, 20f) : Vector3.zero;
    }

    private bool IsValidVector(Vector3 v)
    {
        return !float.IsNaN(v.x) && !float.IsNaN(v.y) && !float.IsNaN(v.z) &&
               !float.IsInfinity(v.x) && !float.IsInfinity(v.y) && !float.IsInfinity(v.z);
    }

    private void ApplyIdlePenalty()
    {
        float currentSpeed = rb.linearVelocity.magnitude;

        if (currentSpeed < idleThreshold)
        {
            idleFramesCount++;
            if (idleFramesCount >= idleFramesRequired)
            {
                AddReward(idlePenalty);
                if (stepCount % 50 == 0) // Логируем не каждый кадр
                {
                    Debug.Log($"[RollerAgent] Step {stepCount} — ШТРАФ ЗА БЕЗДЕЙСТВИЕ: {idlePenalty:F3}, " +
                              $"speed={currentSpeed:F3}, idleFrames={idleFramesCount}");
                }
            }
        }
        else
        {
            idleFramesCount = 0;
        }
        lastVelocityMagnitude = currentSpeed;
    }

    public override void OnActionReceived(ActionBuffers actions)
    {
        stepCount++;

        // Получение сырых действий
        float rawActionX = actions.ContinuousActions[0];
        float rawActionZ = actions.ContinuousActions[1];

        // Safeguard: проверка на NaN
        if (float.IsNaN(rawActionX) || float.IsNaN(rawActionZ))
        {
            Debug.LogWarning($"[RollerAgent] NaN в действиях на шаге {stepCount}! Использую 0");
            rawActionX = 0f;
            rawActionZ = 0f;
        }

        Vector3 rawAction = new Vector3(rawActionX, 0, rawActionZ);

        // Low-pass filter для сглаживания действий
        if (!isFilterInitialized)
        {
            filteredAction = rawAction;
            isFilterInitialized = true;
        }
        else
        {
            filteredAction = filteredAction * actionSmoothing + rawAction * (1f - actionSmoothing);
        }

        // Применение силы
        Vector3 force = filteredAction * moveSpeed;
        rb.AddForce(force);

        // Штраф за каждый шаг
        AddReward(stepPenalty);

        // Штраф за бездействие
        ApplyIdlePenalty();

        // Reward shaping с отслеживанием смены цели
        if (gameManager != null)
        {
            Vector3 currentGoal = gameManager.GetNextCrumbPosition();

            // Проверка: не сменилась ли цель?
            if (Vector3.Distance(currentGoal, lastGoalPosition) > 0.5f)
            {
                // Цель сменилась (собран куб) — пересчитываем расстояние
                prevDistanceToGoal = Vector3.Distance(transform.position, currentGoal);
                lastGoalPosition = currentGoal;
                Debug.Log($"[RollerAgent] Step {stepCount} — ЦЕЛЬ СМЕНИЛАСЬ! Новая дистанция: {prevDistanceToGoal:F2}");
            }

            float currentDistance = Vector3.Distance(transform.position, currentGoal);
            // Clamp расстояния для защиты
            currentDistance = Mathf.Clamp(currentDistance, 0f, 50f);

            float distDelta = prevDistanceToGoal - currentDistance;

            // Применяем shaping только если изменение значительное
            if (Mathf.Abs(distDelta) >= shapingMinDelta)
            {
                float shapeReward = distDelta * shapingGain;
                AddReward(shapeReward);
                if (gameManager != null) gameManager.debugRewardShape += shapeReward;

                if (stepCount % 100 == 0)
                {
                    Debug.Log($"[RollerAgent] ShapeReward: delta={distDelta:F3}, reward={shapeReward:F4}");
                }
            }

            prevDistanceToGoal = currentDistance;
            lastGoalPosition = currentGoal;

            // Логирование каждые 500 шагов
            if (stepCount % 500 == 0)
            {
                Debug.Log($"[RollerAgent] Step {stepCount} — act:({rawActionX:F2},{rawActionZ:F2}) -> filtered:({filteredAction.x:F2},{filteredAction.z:F2}), " +
                          $"dist:{currentDistance:F2}, total reward:{GetCumulativeReward():F2}, vel:{rb.linearVelocity.magnitude:F2}");
            }
        }

        // Падение
        if (transform.localPosition.y < -2f)
        {
            Debug.Log($"[RollerAgent] Step {stepCount} — ПАДЕНИЕ! y={transform.localPosition.y:F2}, штраф {fallPenalty}, total reward={GetCumulativeReward():F2}");
            AddReward(fallPenalty);
            EndEpisode();
        }
    }

    public override void Heuristic(in ActionBuffers actionsOut)
    {
        var continuousActionsOut = actionsOut.ContinuousActions;
        continuousActionsOut[0] = Input.GetAxis("Horizontal");
        continuousActionsOut[1] = Input.GetAxis("Vertical");
    }

    void OnTriggerEnter(Collider other)
    {
        // Safeguard: защита от двойного сбора одного куба
        if (lastCollectedGreen == other && other.CompareTag("GreenCube"))
        {
            Debug.LogWarning($"[RollerAgent] Попытка двойного сбора зелёного куба, игнорирую");
            return;
        }

        if (other.CompareTag("GreenCube"))
        {
            int cubeIndex = gameManager != null ? Mathf.RoundToInt(gameManager.GetProgress() * 4f) : 0;
            // Прогрессивная награда: базовая + бонус за номер куба
            float reward = greenCubeBaseReward + cubeIndex * greenCubeProgressiveBonus;
            AddReward(reward);
            Debug.Log($"[RollerAgent] Step {stepCount} — СОБРАЛ ЗЕЛЁНЫЙ #{cubeIndex + 1}! +{reward:F2}, total reward={GetCumulativeReward():F2}");

            lastCollectedGreen = other;
            other.gameObject.SetActive(false);
            gameManager?.OnGreenCubeCollected();
            if (gameManager != null) gameManager.debugRewardGreen += reward;
        }
        else if (other.CompareTag("RedCube"))
        {
            AddReward(redCubePenalty);
            Debug.Log($"[RollerAgent] Step {stepCount} — ВРЕЗАЛСЯ В КРАСНЫЙ! {redCubePenalty:F2}, total reward={GetCumulativeReward():F2}");
            other.gameObject.SetActive(false);
            gameManager?.OnRedCubeCollected();
            if (gameManager != null) gameManager.debugRewardRed += Mathf.Abs(redCubePenalty);
        }
        else if (other.CompareTag("Target"))
        {
            if (gameManager != null && gameManager.IsTargetReachable)
            {
                AddReward(targetReward);
                Debug.Log($"[RollerAgent] Step {stepCount} — ФИНИШ! +{targetReward:F2}, ИТОГО={GetCumulativeReward():F2}");
                if (gameManager != null) gameManager.debugRewardFinal += targetReward;
                EndEpisode();
            }
        }
    }
}