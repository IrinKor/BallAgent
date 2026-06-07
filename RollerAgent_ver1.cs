using UnityEngine;
using Unity.MLAgents;
using Unity.MLAgents.Sensors;
using Unity.MLAgents.Actuators;

public class RollerAgent : Agent
{
    public float moveSpeed = 10f;
    public bool observeRedCubes = true;

    private Rigidbody rb;
    private Vector3 startPos;
    private GameManager gameManager;
    private Transform target;

    // Для reward shaping
    private float prevDistanceToGoal;
    private int stepCount;

    public void SetGameManager(GameManager m) => gameManager = m;
    public void SetTarget(Transform t) => target = t;

    public override void Initialize()
    {
        rb = GetComponent<Rigidbody>();
        startPos = transform.position;
        if (gameManager == null) gameManager = FindAnyObjectByType<GameManager>();
        Debug.Log($"[RollerAgent] Initialize — startPos: {startPos}, moveSpeed: {moveSpeed}, observeRedCubes: {observeRedCubes}");
    }

    public override void OnEpisodeBegin()
    {
        stepCount = 0;
        transform.position = startPos + new Vector3(Random.Range(-1f, 1f), 0, Random.Range(-1f, 1f));
        transform.rotation = Quaternion.identity;
        rb.linearVelocity = Vector3.zero;
        rb.angularVelocity = Vector3.zero;
        gameManager?.OnEpisodeStart();

        // Инициализация расстояния для reward shaping
        Vector3 goal = gameManager != null ? gameManager.GetNextCrumbPosition() : transform.position;
        prevDistanceToGoal = Vector3.Distance(transform.position, goal);
        Debug.Log($"[RollerAgent] OnEpisodeBegin — позиция: {transform.position}, цель: {goal}, дистанция: {prevDistanceToGoal:F2}");
    }

    public override void CollectObservations(VectorSensor sensor)
    {
        Vector3 myPos = transform.localPosition;
        Vector3 targetPos = target != null ? target.localPosition : Vector3.zero;
        Vector3 vel = rb.linearVelocity;
        Vector3 toNext = gameManager != null ? gameManager.GetNextCrumbPosition() - transform.position : Vector3.zero;
        Vector3 toSecond = gameManager != null ? gameManager.GetSecondCrumbPosition() - transform.position : Vector3.zero;
        float progress = gameManager != null ? gameManager.GetProgress() : 0f;
        float targetOpen = gameManager != null && gameManager.IsTargetReachable ? 1f : 0f;

        sensor.AddObservation(myPos);                                    // 3
        sensor.AddObservation(targetPos);                                // 3
        sensor.AddObservation(vel.x);                                    // 1
        sensor.AddObservation(vel.z);                                    // 1
        sensor.AddObservation(vel.magnitude);                            // 1
        sensor.AddObservation(toNext);                                   // 3
        sensor.AddObservation(toSecond);                                 // 3
        sensor.AddObservation(progress);                                 // 1
        sensor.AddObservation(targetOpen);                               // 1
        // Итого: 17 (без красных)

        // Красные кубы — только если включены
        if (observeRedCubes)
        {
            Vector3[] redPositions = gameManager != null ? gameManager.GetNearbyRedCubePositions(3) : new Vector3[3];
            for (int i = 0; i < 3; i++)
            {
                sensor.AddObservation(redPositions[i]);                  // 9
            }
            // Итого с красными: 26
        }
    }

    public override void OnActionReceived(ActionBuffers actions)
    {
        stepCount++;
        float actionX = actions.ContinuousActions[0];
        float actionZ = actions.ContinuousActions[1];
        Vector3 force = new Vector3(actionX, 0, actionZ) * moveSpeed;
        rb.AddForce(force);

        // Маленький штраф за время — мотивирует не стоять на месте
        AddReward(-0.0005f);

        // Reward shaping
        if (gameManager != null)
        {
            Vector3 goal = gameManager.GetNextCrumbPosition();
            float currentDistance = Vector3.Distance(transform.position, goal);
            float distDelta = prevDistanceToGoal - currentDistance;
            float shapeReward = distDelta * 0.05f;
            AddReward(shapeReward);
            gameManager.debugRewardShape += shapeReward;
            prevDistanceToGoal = currentDistance;

            if (stepCount % 500 == 0)
            {
                Debug.Log($"[RollerAgent] Step {stepCount} Action — act:({actionX:F2},{actionZ:F2}), dist:{currentDistance:F2}, delta:{distDelta:F3}, reward:{shapeReward:F4}, total:{GetCumulativeReward():F2}, vel:{rb.linearVelocity.magnitude:F2}");
            }
        }

        // Падение
        if (transform.localPosition.y < -2f)
        {
            Debug.Log($"[RollerAgent] Step {stepCount} — ПАДЕНИЕ! y={transform.localPosition.y:F2}, штраф -5, total reward={GetCumulativeReward():F2}");
            AddReward(-5f);
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
        if (other.CompareTag("GreenCube"))
        {
            int cubeIndex = gameManager != null ? Mathf.RoundToInt(gameManager.GetProgress() * 4f) : 0;
            float reward = 30f + cubeIndex * 20f; // 30, 50, 70, 90
            AddReward(reward);
            Debug.Log($"[RollerAgent] Step {stepCount} — СОБРАЛ ЗЕЛЁНЫЙ #{cubeIndex + 1}! +{reward}, total reward={GetCumulativeReward():F2}, позиция={transform.position}");
            other.gameObject.SetActive(false);
            gameManager?.OnGreenCubeCollected();
            if (gameManager != null) gameManager.debugRewardGreen += reward;
        }
        else if (other.CompareTag("RedCube"))
        {
            AddReward(-15f);
            Debug.Log($"[RollerAgent] Step {stepCount} — ВРЕЗАЛСЯ В КРАСНЫЙ! -15, total reward={GetCumulativeReward():F2}, позиция={transform.position}");
            other.gameObject.SetActive(false);
            gameManager?.OnRedCubeCollected();
            if (gameManager != null) gameManager.debugRewardRed += 15f;
        }
        else if (other.CompareTag("Target"))
        {
            if (gameManager != null && gameManager.IsTargetReachable)
            {
                AddReward(100f);
                Debug.Log($"[RollerAgent] Step {stepCount} — ФИНИШ! +100, ИТОГО={GetCumulativeReward():F2}, позиция={transform.position}");
                if (gameManager != null) gameManager.debugRewardFinal += 100f;
                EndEpisode();
            }
        }
    }
}
