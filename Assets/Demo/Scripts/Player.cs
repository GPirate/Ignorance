using UnityEngine;
using Mirror;

namespace Ignorance.Examples
{
    public class Player : NetworkBehaviour
    {
        [SerializeField] float speed = 30;
        [SerializeField] Rigidbody2D rigidbody2d;

        // need to use FixedUpdate for rigidbody
        void FixedUpdate()
        {
            // only let the local player control the racket.
            // don't control other player's rackets
            if (isLocalPlayer)
                rigidbody2d.velocity = new Vector2(0, Input.GetAxisRaw("Vertical")) * speed * Time.fixedDeltaTime;
        }
    }
}
