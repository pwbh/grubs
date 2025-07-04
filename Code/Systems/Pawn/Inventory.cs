﻿using Grubs.Common;
using Grubs.Equipment;
using Grubs.Equipment.Weapons;
using Grubs.Pawn;
using Grubs.Systems.Pawn.Grubs;

namespace Grubs.Systems.Pawn;

[Title( "Grubs - Player Inventory" ), Category( "Grubs/Pawn" )]
public sealed class Inventory : LocalComponent<Inventory>
{
	private static readonly Logger Log = new("Inventory");
	
	[Property] public required List<GameObject> EquipmentPrefabs { get; set; } = new();
	[Property] public required Player Player { get; set; }

	public List<Equipment.Equipment> Equipment { get; set; } = new();
	public Equipment.Equipment ActiveEquipment => GetActiveEquipment();
	[Property, ReadOnly, Sync] public int ActiveSlot { get; set; }
	[Property, ReadOnly, Sync] public bool EquipmentActive { get; set; }

	public bool InventoryOpen { get; set; }
	public bool IsClosing { get; set; }
	
	protected override void OnStart()
	{
		if ( IsProxy )
			return;

		Local = this;
	}
	
	public void Cleanup()
	{
		foreach (var t in Equipment)
			t?.GameObject?.Destroy();

		Equipment.Clear();
	}

	[Rpc.Owner( NetFlags.HostOnly )]
	public void InitializeWeapons()
	{
		if ( IsProxy )
			return;

		var infiniteAmmo = GrubsConfig.InfiniteAmmo;
	
		EquipmentActive = false;
	
		foreach ( var prefab in EquipmentPrefabs )
		{
			var go = prefab.Clone();
			go.NetworkSpawn();
	
			var equipment = go.Components.Get<Equipment.Equipment>();
			if ( !equipment.IsValid() )
			{
				Log.Error( "Newly spawned equipment component is invalid." );
				return;
			}
	
			Equipment.Add( equipment );
	
			var slotIndex = Equipment.Count - 1;
	
			if ( infiniteAmmo )
				equipment.Ammo = -1;
	
			if ( !Player.IsValid() )
			{
				Log.Error( "Player's active grub is invalid - this is probably a networking bug." );
				return;
			}
	
			equipment.SlotIndex = slotIndex;
			equipment.Initialize( Player );
			equipment.Holster();
		}
	
		// Sort by drop chance (0 drop chance gets put to the front)
		Equipment = Equipment.OrderBy( x => x.Data.DropChance != 0 ? -x.Data.DropChance : -100f ).ToList();
		// Put the tools at the front of the line
		Equipment = Equipment.OrderBy( x => x.Data.Type != EquipmentType.Tool ).ToList();
	
		// Re-set equipment slot indexes
		foreach ( var item in Equipment )
		{
			item.SlotIndex = Equipment.IndexOf( item );
		}
	}

	protected override void OnUpdate()
	{
		base.OnUpdate();

		if ( IsProxy )
			return;
	
		if ( Input.Pressed( "toggle_inventory" ) )
		{
			Log.Info( $"Toggle inventory to: {!InventoryOpen}" );
			InventoryOpen = !InventoryOpen;
		}
	
		if ( Input.UsingController && InventoryOpen && Input.Released( "backflip" ) )
		{
			InventoryOpen = false;
			IsClosing = true;
			return;
		}
	
		if ( Input.Pressed( "next_equipment" ) )
		{
			CycleSlot();
		}
	
		if ( Input.Pressed( "previous_equipment" ) )
		{
			CycleSlot( false );
		}
	
		IsClosing = false;
	}

	// kidd: Called by PlayerInventory.razor, VS references won't pop.
	public void EquipItem( Equipment.Equipment equipment )
	{
		if ( Player.HasFiredThisTurn )
			return;
	
		var active = GetActiveEquipment();
		if ( active?.Components.TryGet<Weapon>( out var weapon ) ?? false )
		{
			if ( (weapon.IsFiring && !weapon.CanSwapDuringUse) || (weapon.TimesUsed > 0 && !weapon.CanSwapAfterUse) )
				return;
		}

		HolsterActive();
		var index = Equipment.IndexOf( equipment );
		if ( index == -1 )
			return;
		ActiveSlot = index;
		Equip( ActiveSlot );
	
		InventoryOpen = false;
	}

	public void HolsterActive()
	{
		Log.Info( $"Attempting to holster active weapon." );
		Holster( ActiveSlot );
	}
	
	[Rpc.Broadcast]
	public void Equip( int slot )
	{
		if ( !Player.IsActive )
			return;
	
		var equipment = GetHolsteredEquipment( slot );
		if ( equipment is null || !equipment.IsValid() )
		{
			Log.Warning( $"Can't equip: nothing at slot {slot} or is null" );
			return;
		}
	
		EquipmentActive = true;
		equipment.Deploy( Player.ActiveGrub );
	}

	[Rpc.Broadcast]
	public void Holster( int slot )
	{
		if ( !Player.IsValid() )
			return;
		
		Log.Info( $"Holstering {slot} for {Player}." );
		
		var equipment = GetActiveEquipment( slot );

		if ( equipment is null || !equipment.IsValid() )
			return;

		EquipmentActive = false;
		equipment.Grub?.ActiveMountable?.Dismount();
		equipment.Holster();
	}

	private Equipment.Equipment GetActiveEquipment()
	{
		return !EquipmentActive ? null : GetActiveEquipment( ActiveSlot );
	}

	public int GetNextSlot()
	{
		if ( ActiveSlot >= Equipment.Count - 1 )
			return 0;
		return ActiveSlot + 1;
	}

	public int GetPrevSlot()
	{
		if ( ActiveSlot == 0 )
			return Equipment.Count - 1;
		return ActiveSlot - 1;
	}

	private Equipment.Equipment GetHolsteredEquipment( int slot )
	{
		return Player.GameObject.Components
			.GetAll<Equipment.Equipment>( FindMode.EverythingInSelfAndDescendants )
			.FirstOrDefault( x => x.SlotIndex == slot );
	}

	private Equipment.Equipment GetActiveEquipment( int slot )
	{
		if ( !Player.IsValid() || !Player.ActiveGrub.IsValid() )
			return null;

		return Player.ActiveGrub.GameObject.Components
			.GetAll<Equipment.Equipment>( FindMode.EverythingInSelfAndDescendants )
			.FirstOrDefault( x => x.SlotIndex == slot );
	}

	private void CycleSlot( bool forwards = true )
	{
		var active = GetActiveEquipment();
		if ( active?.Components.TryGet<Weapon>( out var weapon ) ?? false )
		{
			if ( weapon.IsFiring && !weapon.CanSwapDuringUse || weapon.TimesUsed > 0 && !weapon.CanSwapAfterUse )
				return;
		}
	
		HolsterActive();
		var slot = forwards ? GetNextSlot() : GetPrevSlot();
		ActiveSlot = slot;
		Equip( ActiveSlot );
	}

	public override int GetHashCode()
	{
		return HashCode.Combine( Equipment.Sum( e => e.Ammo ), InventoryOpen );
	}
}
