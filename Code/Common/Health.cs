﻿using Grubs.Equipment.Gadgets.Ground;
using Grubs.Equipment.Gadgets.Projectiles;
using Grubs.Helpers;
using Grubs.Pawn;
using Grubs.Systems.GameMode;
using Grubs.Systems.Particles;
using Grubs.Systems.Pawn;
using Grubs.Systems.Pawn.Grubs;

namespace Grubs.Common;

public partial class Health : Component
{
	[Property] public float MaxHealth { get; set; }

	[Sync] public float CurrentHealth { get; set; }

	[Sync] public bool DeathInvoked { get; set; } = false;
	
	[Sync] public bool HasBeenDamaged { get; set; }

	public delegate void Death();

	public event Death ObjectDied;

	public delegate void Damaged( GrubsDamageInfo damageInfo );

	public event Damaged ObjectDamaged;

	private Queue<GrubsDamageInfo> DamageQueue { get; set; } = new();

	private DeathReason _deathReason;

	protected override void OnStart()
	{
		CurrentHealth = MaxHealth;
	}

	public void TakeDamage( GrubsDamageInfo damageInfo, bool immediate = false )
	{
		if ( !this.IsValid() || Components is null )
			return;
		
		if ( Components.TryGet( out Grub grub ) )
		{
			if ( !grub.IsValid() )
				return;
			
			if ( BaseGameMode.Current.IsValid() && BaseGameMode.Current.IsGrubActive( grub ) )
				BaseGameMode.Current.GrubDamaged( grub );
			
			if ( !immediate )
			{
				QueueDamage( damageInfo, grub );
				return;
			}
		}

		CurrentHealth -= damageInfo.Damage;

		ObjectDamaged?.Invoke( damageInfo );

		if ( CurrentHealth <= 0 && !DeathInvoked )
		{
			var isKillZoneDeath = damageInfo.Tags.Has( "killzone" );
			if ( isKillZoneDeath )
			{
				var damageInfos = new List<GrubsDamageInfo>();
				var lastReason = DamageQueue.LastOrDefault();
				if ( !lastReason.Equals( default( GrubsDamageInfo ) ) ) // Get previous damage context if it exists.
					damageInfos.Add( lastReason );

				damageInfos.Add( damageInfo );
				_deathReason = DeathReason.FindReason( grub, damageInfos );
				
				if ( grub.IsValid() )
					WorldPopupHelper.Instance.CreateKillZoneDeathIndicator( damageInfo.WorldPosition );
			}

			if ( grub.IsValid() )
			{
				Log.Info( $"{grub.Name} died, handling death." );
				GrubDiedRpc( grub );
			}
			
			_ = OnDeath( isKillZoneDeath );
		}
	}

	private void QueueDamage( GrubsDamageInfo damageInfo, Grub grub )
	{
		if ( Networking.IsHost && BaseGameMode.Current.IsValid() )
			BaseGameMode.Current?.GrubDamaged( grub );

		HasBeenDamaged = true;
		DamageQueue.Enqueue( damageInfo );
	}

	[Rpc.Host]
	private void GrubDamagedRpc( Grub grub )
	{
		BaseGameMode.Current.GrubDamaged( grub );
	}

	[Rpc.Host]
	private void GrubDiedRpc( Grub grub )
	{
		BaseGameMode.Current.GrubDied( grub );
	}

	/// <summary>
	/// Will dequeue DamageQueue until empty and apply any damage to CurrentHealth.
	/// </summary>
	[Rpc.Owner]
	public void ApplyDamage()
	{
		if ( DamageQueue.Count == 0 )
		{
			Log.Info( "Nothing in DamageQueue." );
			return;
		}
		
		HasBeenDamaged = false;

		var totalDamage = 0f;
		var damageInfos = new List<GrubsDamageInfo>();
		while ( DamageQueue.TryDequeue( out var info ) )
		{
			totalDamage += info.Damage;
			damageInfos.Add( info );
		}

		if ( totalDamage >= CurrentHealth && Components.TryGet( out Grub grub ) )
			_deathReason = DeathReason.FindReason( grub, damageInfos );

		WorldPopupHelper.Instance.CreateDamagePopup( GameObject.Id, totalDamage );
		TakeDamage( new GrubsDamageInfo( totalDamage, Guid.Empty ), true );
	}

	public void Heal( float heal )
	{
		CurrentHealth += heal;
		WorldPopupHelper.Instance.CreateDamagePopup( GameObject.Id, -heal );
	}

	private async Task OnDeath( bool deleteImmediately = false )
	{
		if ( Components.TryGet( out Grub grub ) )
		{
			if ( !grub.IsValid() )
				return;

			if ( !deleteImmediately )
			{
				await GameTask.Delay( 500 ); // Give clients some time to update GrubTag healthbar to 0 before we play death animation.
				DeathInvoked = true;

				// Double check that grub is still valid, since we have waited 500ms since fetching the component.
				if ( !grub.IsValid() )
					return;

				// This is shit, especially since we want a variety of death animations in the future.
				// Just don't know where to put this right now.
				var plungerPrefab = ResourceLibrary.Get<PrefabFile>( "prefabs/world/dynamite_plunger.prefab" );
				var position = grub.WorldPosition;
				var plunger = SceneUtility.GetPrefabScene( plungerPrefab ).Clone();
				plunger.NetworkSpawn();
				plunger.WorldPosition = grub.PlayerController.Facing == -1 ? position - new Vector3( 30, 0, 0 ) : position;

				await GameTask.Delay( 750 );

				if ( !grub.IsValid() )
					return;

				// Same as above.
				DeathEffects( position );

				if ( _deathReason.FromDisconnect )
					ExplosionHelper.Instance.Explode( this, position, 40f, 15f, grub.Id, grub.Name );
				else
					ExplosionHelper.Instance.Explode( this, position, 75f, 25f, grub.Id, grub.Name );

				plunger?.Destroy();

				var gravePrefab = ResourceLibrary.Get<PrefabFile>( "prefabs/world/drops/gravestone.prefab" );
				var grave = SceneUtility.GetPrefabScene( gravePrefab ).Clone();
				grave.NetworkSpawn();
				grave.WorldPosition = position.WithY( 536f ).WithZ( position.z + 24 );
			}

			ChatHelper.Instance.SendInfoMessage( _deathReason.ToString() );

			var attackerGuid = _deathReason.SecondInfo.AttackerGuid;
			if ( _deathReason.FromKillTrigger ) // If we've died to a kill trigger, check if we have additional damage to credit, otherwise we've attacked ourselves.
				attackerGuid = _deathReason.FirstReason != DamageType.None ? _deathReason.FirstInfo.AttackerGuid : grub.Id;

			// var attacker = Scene.GetAllComponents<Player>().FirstOrDefault( p => p.Grubs.Contains( attackerGuid ) );
			// var connection = attacker?.Network.Owner;
			// using ( Rpc.FilterInclude( connection ) )
			// {
			// 	if ( grub.Owner.IsValid() )
			// 		Stats.IncrementGrubsKilled( grub.Owner.Id );
			// }
			
			if ( grub.Owner?.Inventory.IsValid() ?? false )
				grub.Owner.Inventory.HolsterActive();
			grub.GameObject.Destroy();
		}

		ObjectDied?.Invoke();

		DeathInvoked = true;

		if ( Components.TryGet( out ExplosiveProjectile explosive ) && explosive.ExplodeOnDeath )
		{
			explosive.Explode();
		}

		if ( Components.TryGet( out ProximityExplosive proximity, FindMode.EverythingInSelfAndChildren ) && proximity.DetonateOnDeath && !proximity.IsDetonating )
		{
			proximity.StartDetonating();
		}
	}

	[Rpc.Broadcast]
	private void DeathEffects( Vector3 position )
	{
		ExplosionParticles.Spawn()
			.SetWorldPosition( WorldPosition )
			.SetScale( 100f );
		Sound.Play( "explosion_short_tail", position );
	}
}
