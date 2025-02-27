using System.Linq;
using Content.Server.Administration.Logs;
using System.Numerics;
using Content.Server.Advertise;
using Content.Server.Cargo.Systems;
using Content.Server.Chat.Systems;
using Content.Server.Emp;
using Content.Server.Power.Components;
using Content.Server.Power.EntitySystems;
using Content.Server.UserInterface;
using Content.Shared.Access.Components;
using Content.Shared.Access.Systems;
using Content.Shared.Actions;
using Content.Shared.Damage;
using Content.Shared.Database;
using Content.Shared.Destructible;
using Content.Shared.DoAfter;
using Content.Shared.Emag.Components;
using Content.Shared.Emag.Systems;
using Content.Shared.Emp;
using Content.Shared.Hands.Components;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Interaction;
using Content.Shared.Popups;
using Content.Shared.Throwing;
using Content.Shared.UserInterface;
using Content.Shared.VendingMachines;
using Robust.Server.GameObjects;
using Robust.Shared.Audio;
using Robust.Shared.Containers;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;
using Robust.Shared.Timing;
using Robust.Shared.Utility;

namespace Content.Server.VendingMachines
{
    public sealed class VendingMachineSystem : SharedVendingMachineSystem
    {
        [Dependency] private readonly IRobustRandom _random = default!;
        [Dependency] private readonly AccessReaderSystem _accessReader = default!;
        [Dependency] private readonly AppearanceSystem _appearanceSystem = default!;
        [Dependency] private readonly SharedActionsSystem _action = default!;
        [Dependency] private readonly PricingSystem _pricing = default!;
        [Dependency] private readonly ThrowingSystem _throwingSystem = default!;
        [Dependency] private readonly UserInterfaceSystem _userInterfaceSystem = default!;
        [Dependency] private readonly IGameTiming _timing = default!;
        [Dependency] private readonly AdvertiseSystem _advertise = default!;
        [Dependency] private readonly SharedContainerSystem _containerSystem = default!;
        [Dependency] private readonly SharedHandsSystem _handsSystem = default!;
        [Dependency] private readonly IAdminLogManager _adminLogger = default!;

        private ISawmill _sawmill = default!;

        public override void Initialize()
        {
            base.Initialize();

            _sawmill = Logger.GetSawmill("vending");
            SubscribeLocalEvent<VendingMachineComponent, MapInitEvent>(OnComponentMapInit);
            SubscribeLocalEvent<VendingMachineComponent, PowerChangedEvent>(OnPowerChanged);
            SubscribeLocalEvent<VendingMachineComponent, BreakageEventArgs>(OnBreak);
            SubscribeLocalEvent<VendingMachineComponent, GotEmaggedEvent>(OnEmagged);
            SubscribeLocalEvent<VendingMachineComponent, DamageChangedEvent>(OnDamage);
            SubscribeLocalEvent<VendingMachineComponent, PriceCalculationEvent>(OnVendingPrice);
            SubscribeLocalEvent<VendingMachineComponent, EmpPulseEvent>(OnEmpPulse);
            SubscribeLocalEvent<VendingMachineComponent, AfterInteractUsingEvent>(HandleAfterInteractUsing); //SS220 vending-storage

            SubscribeLocalEvent<VendingMachineComponent, ActivatableUIOpenAttemptEvent>(OnActivatableUIOpenAttempt);

            Subs.BuiEvents<VendingMachineComponent>(VendingMachineUiKey.Key, subs =>
            {
                subs.Event<BoundUIOpenedEvent>(OnBoundUIOpened);
                subs.Event<BoundUIClosedEvent>(OnBoundUIClosed);
                subs.Event<VendingMachineEjectMessage>(OnInventoryEjectMessage);
            });

            SubscribeLocalEvent<VendingMachineComponent, VendingMachineSelfDispenseEvent>(OnSelfDispense);

            SubscribeLocalEvent<VendingMachineComponent, RestockDoAfterEvent>(OnDoAfter);

            SubscribeLocalEvent<VendingMachineRestockComponent, PriceCalculationEvent>(OnPriceCalculation);
        }

        private void OnComponentMapInit(EntityUid uid, VendingMachineComponent component, MapInitEvent args)
        {
            _action.AddAction(uid, ref component.ActionEntity, component.Action, uid);
            Dirty(uid, component);
        }

        private void OnVendingPrice(EntityUid uid, VendingMachineComponent component, ref PriceCalculationEvent args)
        {
            var price = 0.0;

            foreach (var entry in component.Inventory.Values)
            {
                if (!PrototypeManager.TryIndex<EntityPrototype>(entry.ID, out var proto))
                {
                    _sawmill.Error($"Unable to find entity prototype {entry.ID} on {ToPrettyString(uid)} vending.");
                    continue;
                }

                price += entry.Amount * _pricing.GetEstimatedPrice(proto);
            }

            args.Price += price;
        }

        protected override void OnComponentInit(EntityUid uid, VendingMachineComponent component, ComponentInit args)
        {
            base.OnComponentInit(uid, component, args);

            if (HasComp<ApcPowerReceiverComponent>(uid))
            {
                TryUpdateVisualState(uid, component);
            }

            component.Container = _containerSystem.EnsureContainer<Container>(uid, VendingMachineComponent.ContainerId);
        }

        private void OnActivatableUIOpenAttempt(EntityUid uid, VendingMachineComponent component, ActivatableUIOpenAttemptEvent args)
        {
            if (component.Broken)
                args.Cancel();
        }

        private void OnBoundUIOpened(EntityUid uid, VendingMachineComponent component, BoundUIOpenedEvent args)
        {
            UpdateVendingMachineInterfaceState(uid, component);
        }

        private void OnBoundUIClosed(EntityUid uid, VendingMachineComponent component, BoundUIClosedEvent args)
        {
            // Only vendors that advertise will send message after dispensing
            if (component.ShouldSayThankYou && TryComp<AdvertiseComponent>(uid, out var advertise))
            {
                _advertise.SayThankYou(uid, advertise);
                component.ShouldSayThankYou = false;
            }
        }

        private void UpdateVendingMachineInterfaceState(EntityUid uid, VendingMachineComponent component)
        {
            var state = new VendingMachineInterfaceState(GetAllInventory(uid, component));

            _userInterfaceSystem.TrySetUiState(uid, VendingMachineUiKey.Key, state);
        }

        private void OnInventoryEjectMessage(EntityUid uid, VendingMachineComponent component, VendingMachineEjectMessage args)
        {
            if (!this.IsPowered(uid, EntityManager))
                return;

            if (args.Session.AttachedEntity is not { Valid: true } entity || Deleted(entity))
                return;

            AuthorizedVend(uid, entity, args.Type, args.ID, component);
        }

        private void OnPowerChanged(EntityUid uid, VendingMachineComponent component, ref PowerChangedEvent args)
        {
            TryUpdateVisualState(uid, component);
        }

        private void OnBreak(EntityUid uid, VendingMachineComponent vendComponent, BreakageEventArgs eventArgs)
        {
            vendComponent.Broken = true;
            TryUpdateVisualState(uid, vendComponent);
        }

        private void OnEmagged(EntityUid uid, VendingMachineComponent component, ref GotEmaggedEvent args)
        {
            // only emag if there are emag-only items
            args.Handled = component.EmaggedInventory.Count > 0;
        }

        private void OnDamage(EntityUid uid, VendingMachineComponent component, DamageChangedEvent args)
        {
            if (component.Broken || component.DispenseOnHitCoolingDown ||
                component.DispenseOnHitChance == null || args.DamageDelta == null)
                return;

            if (args.DamageIncreased && args.DamageDelta.GetTotal() >= component.DispenseOnHitThreshold &&
                _random.Prob(component.DispenseOnHitChance.Value))
            {
                if (component.DispenseOnHitCooldown > 0f)
                    component.DispenseOnHitCoolingDown = true;
                EjectRandom(uid, throwItem: true, forceEject: true, component);
            }
        }

        private void OnSelfDispense(EntityUid uid, VendingMachineComponent component, VendingMachineSelfDispenseEvent args)
        {
            if (args.Handled)
                return;

            args.Handled = true;
            EjectRandom(uid, throwItem: true, forceEject: false, component);
        }

        private void OnDoAfter(EntityUid uid, VendingMachineComponent component, DoAfterEvent args)
        {
            if (args.Handled || args.Cancelled || args.Args.Used == null)
                return;

            if (!TryComp<VendingMachineRestockComponent>(args.Args.Used, out var restockComponent))
            {
                _sawmill.Error($"{ToPrettyString(args.Args.User)} tried to restock {ToPrettyString(uid)} with {ToPrettyString(args.Args.Used.Value)} which did not have a VendingMachineRestockComponent.");
                return;
            }

            TryRestockInventory(uid, component);

            Popup.PopupEntity(Loc.GetString("vending-machine-restock-done", ("this", args.Args.Used), ("user", args.Args.User), ("target", uid)), args.Args.User, PopupType.Medium);

            Audio.PlayPvs(restockComponent.SoundRestockDone, uid, AudioParams.Default.WithVolume(-2f).WithVariation(0.2f));

            Del(args.Args.Used.Value);

            args.Handled = true;
        }

        /// <summary>
        /// Sets the <see cref="VendingMachineComponent.CanShoot"/> property of the vending machine.
        /// </summary>
        public void SetShooting(EntityUid uid, bool canShoot, VendingMachineComponent? component = null)
        {
            if (!Resolve(uid, ref component))
                return;

            component.CanShoot = canShoot;
        }

        public void Deny(EntityUid uid, VendingMachineComponent? vendComponent = null)
        {
            if (!Resolve(uid, ref vendComponent))
                return;

            if (vendComponent.Denying)
                return;

            vendComponent.Denying = true;
            Audio.PlayPvs(vendComponent.SoundDeny, uid, AudioParams.Default.WithVolume(-2f));
            TryUpdateVisualState(uid, vendComponent);
        }

        /// <summary>
        /// Checks if the user is authorized to use this vending machine
        /// </summary>
        /// <param name="uid"></param>
        /// <param name="sender">Entity trying to use the vending machine</param>
        /// <param name="vendComponent"></param>
        public bool IsAuthorized(EntityUid uid, EntityUid sender, VendingMachineComponent? vendComponent = null)
        {
            if (!Resolve(uid, ref vendComponent))
                return false;

            if (!TryComp<AccessReaderComponent>(uid, out var accessReader))
                return true;

            if (_accessReader.IsAllowed(sender, uid, accessReader) || HasComp<EmaggedComponent>(uid))
                return true;

            Popup.PopupEntity(Loc.GetString("vending-machine-component-try-eject-access-denied"), uid);
            Deny(uid, vendComponent);
            return false;
        }

        private void HandleAfterInteractUsing(EntityUid uid, VendingMachineComponent component,
            AfterInteractUsingEvent args)
        {
            if (args.Handled || !args.CanReach)
                return;

            if (!HasComp<HandsComponent>(args.User))
                return;

            if (!IsAuthorized(uid, args.User, component))
                return;

            if (!TryInsertItem(uid, args.User, args.Used, component))
                return;

            _adminLogger.Add(LogType.Action, LogImpact.Medium,
                $"{ToPrettyString(args.User):player} inserted {ToPrettyString(args.Used)} into {ToPrettyString(uid)}");
            args.Handled = true;
        }

        private bool TryInsertItem(EntityUid vendUid, EntityUid userUid, EntityUid entityUid, VendingMachineComponent vendComponent)
        {
            if (!TryGetItemCode(entityUid, out var itemId))
                return false;

            if (!ItemIsInWhitelist(entityUid, itemId, vendComponent))
                return false;

            if (!_handsSystem.IsHolding(userUid, entityUid, out var hand) || !_handsSystem.CanDropHeld(userUid, hand))
                return false;

            if (vendComponent.Ejecting || vendComponent.Broken || !this.IsPowered(vendUid, EntityManager))
                return false;

            if (!_handsSystem.TryDropIntoContainer(userUid, entityUid, vendComponent.Container))
                return false;

            if (vendComponent.Inventory.ContainsKey(itemId) &&
                vendComponent.Inventory.TryGetValue(itemId, out var entry))
            {
                entry.Amount++;
                entry.EntityUids.Add(GetNetEntity(entityUid));
                return true;
            }

            vendComponent.Inventory.Add(itemId,
                new VendingMachineInventoryEntry(InventoryType.Regular, itemId, 1, GetNetEntity(entityUid))
            );

            return true;
        }

        private bool ItemIsInWhitelist(EntityUid item, string itemCode, VendingMachineComponent vendComponent)
        {
            if (vendComponent.Inventory.ContainsKey(itemCode))
                return true;

            return vendComponent.Whitelist != null && vendComponent.Whitelist.IsValid(item);
        }

        private bool TryGetItemCode(EntityUid entityUid, out string code)
        {
            var metadata = IoCManager.Resolve<IEntityManager>().GetComponentOrNull<MetaDataComponent>(entityUid);
            code = metadata?.EntityPrototype?.ID ?? "";
            return !string.IsNullOrEmpty(code);
        }

        /// <summary>
        /// Tries to eject the provided item. Will do nothing if the vending machine is incapable of ejecting, already ejecting
        /// or the item doesn't exist in its inventory.
        /// </summary>
        /// <param name="uid"></param>
        /// <param name="type">The type of inventory the item is from</param>
        /// <param name="itemId">The prototype ID of the item</param>
        /// <param name="throwItem">Whether the item should be thrown in a random direction after ejection</param>
        /// <param name="vendComponent"></param>
        public void TryEjectVendorItem(EntityUid uid, InventoryType type, string itemId, bool throwItem, VendingMachineComponent? vendComponent = null)
        {
            if (!Resolve(uid, ref vendComponent))
                return;

            if (vendComponent.Ejecting || vendComponent.Broken || !this.IsPowered(uid, EntityManager))
            {
                return;
            }

            var entry = GetEntry(uid, itemId, type, vendComponent);

            if (entry == null)
            {
                Popup.PopupEntity(Loc.GetString("vending-machine-component-try-eject-invalid-item"), uid);
                Deny(uid, vendComponent);
                return;
            }

            if (entry.Amount <= 0)
            {
                Popup.PopupEntity(Loc.GetString("vending-machine-component-try-eject-out-of-stock"), uid);
                Deny(uid, vendComponent);
                return;
            }

            if (string.IsNullOrEmpty(entry.ID))
                return;


            // Start Ejecting, and prevent users from ordering while anim playing
            vendComponent.Ejecting = true;
            GetItemToEject(ref vendComponent, entry);
            vendComponent.ThrowNextItem = throwItem;
            vendComponent.ShouldSayThankYou = true;
            entry.Amount--;
            UpdateVendingMachineInterfaceState(uid, vendComponent);
            TryUpdateVisualState(uid, vendComponent);
            Audio.PlayPvs(vendComponent.SoundVend, uid);
        }

        /// <summary>
        /// Checks whether the user is authorized to use the vending machine, then ejects the provided item if true
        /// </summary>
        /// <param name="uid"></param>
        /// <param name="sender">Entity that is trying to use the vending machine</param>
        /// <param name="type">The type of inventory the item is from</param>
        /// <param name="itemId">The prototype ID of the item</param>
        /// <param name="component"></param>
        public void AuthorizedVend(EntityUid uid, EntityUid sender, InventoryType type, string itemId, VendingMachineComponent component)
        {
            if (IsAuthorized(uid, sender, component))
            {
                TryEjectVendorItem(uid, type, itemId, component.CanShoot, component);
            }
        }

        /// <summary>
        /// Tries to update the visuals of the component based on its current state.
        /// </summary>
        public void TryUpdateVisualState(EntityUid uid, VendingMachineComponent? vendComponent = null)
        {
            if (!Resolve(uid, ref vendComponent))
                return;

            var finalState = VendingMachineVisualState.Normal;
            if (vendComponent.Broken)
            {
                finalState = VendingMachineVisualState.Broken;
            }
            else if (vendComponent.Ejecting)
            {
                finalState = VendingMachineVisualState.Eject;
            }
            else if (vendComponent.Denying)
            {
                finalState = VendingMachineVisualState.Deny;
            }
            else if (!this.IsPowered(uid, EntityManager))
            {
                finalState = VendingMachineVisualState.Off;
            }

            _appearanceSystem.SetData(uid, VendingMachineVisuals.VisualState, finalState);
        }

        /// <summary>
        /// Ejects a random item from the available stock. Will do nothing if the vending machine is empty.
        /// </summary>
        /// <param name="uid"></param>
        /// <param name="throwItem">Whether to throw the item in a random direction after dispensing it.</param>
        /// <param name="forceEject">Whether to skip the regular ejection checks and immediately dispense the item without animation.</param>
        /// <param name="vendComponent"></param>
        public void EjectRandom(EntityUid uid, bool throwItem, bool forceEject = false, VendingMachineComponent? vendComponent = null)
        {
            if (!Resolve(uid, ref vendComponent))
                return;

            var availableItems = GetAvailableInventory(uid, vendComponent);
            if (availableItems.Count <= 0)
                return;

            var item = _random.Pick(availableItems);

            if (forceEject)
            {
                GetItemToEject(ref vendComponent, item);
                vendComponent.ThrowNextItem = throwItem;
                var entry = GetEntry(uid, item.ID, item.Type, vendComponent);
                if (entry != null)
                    entry.Amount--;
                EjectItem(uid, vendComponent, forceEject);
            }
            else
            {
                TryEjectVendorItem(uid, item.Type, item.ID, throwItem, vendComponent);
            }
        }

        private void GetItemToEject(ref VendingMachineComponent vendComponent, VendingMachineInventoryEntry item)
        {
            if (item.EntityUids.Count > 0)
            {
                vendComponent.NextEntityToEject = GetEntity(item.EntityUids[0]);
                item.EntityUids.RemoveAt(0);
            }
            else
            {
                vendComponent.NextItemToEject = item.ID;
            }
        }

        private void EjectItem(EntityUid uid, VendingMachineComponent? vendComponent = null, bool forceEject = false)
        {
            if (!Resolve(uid, ref vendComponent))
                return;

            // No need to update the visual state because we never changed it during a forced eject
            if (!forceEject)
                TryUpdateVisualState(uid, vendComponent);

            if (string.IsNullOrEmpty(vendComponent.NextItemToEject) && vendComponent.NextEntityToEject == null)
            {
                vendComponent.ThrowNextItem = false;
                return;
            }

            EntityUid ent;

            if (vendComponent.NextEntityToEject is { } entityUid)
            {
                vendComponent.Container.Remove(entityUid);
                ent = entityUid;
            }
            else
            {
                ent = Spawn(vendComponent.NextItemToEject, Transform(uid).Coordinates);
            }


            if (vendComponent.ThrowNextItem)
            {
                var range = vendComponent.NonLimitedEjectRange;
                var direction = new Vector2(_random.NextFloat(-range, range), _random.NextFloat(-range, range));
                _throwingSystem.TryThrow(ent, direction, vendComponent.NonLimitedEjectForce);
            }

            vendComponent.NextItemToEject = null;
            vendComponent.NextEntityToEject = null; // SS220 vending-storage
            vendComponent.ThrowNextItem = false;
        }

        private VendingMachineInventoryEntry? GetEntry(EntityUid uid, string entryId, InventoryType type, VendingMachineComponent? component = null)
        {
            if (!Resolve(uid, ref component))
                return null;

            if (type == InventoryType.Emagged && HasComp<EmaggedComponent>(uid))
                return component.EmaggedInventory.GetValueOrDefault(entryId);

            if (type == InventoryType.Contraband && component.Contraband)
                return component.ContrabandInventory.GetValueOrDefault(entryId);

            return component.Inventory.GetValueOrDefault(entryId);
        }

        public override void Update(float frameTime)
        {
            base.Update(frameTime);

            var query = EntityQueryEnumerator<VendingMachineComponent>();
            while (query.MoveNext(out var uid, out var comp))
            {
                if (comp.Ejecting)
                {
                    comp.EjectAccumulator += frameTime;
                    if (comp.EjectAccumulator >= comp.EjectDelay)
                    {
                        comp.EjectAccumulator = 0f;
                        comp.Ejecting = false;

                        EjectItem(uid, comp);
                    }
                }

                if (comp.Denying)
                {
                    comp.DenyAccumulator += frameTime;
                    if (comp.DenyAccumulator >= comp.DenyDelay)
                    {
                        comp.DenyAccumulator = 0f;
                        comp.Denying = false;

                        TryUpdateVisualState(uid, comp);
                    }
                }

                if (comp.DispenseOnHitCoolingDown)
                {
                    comp.DispenseOnHitAccumulator += frameTime;
                    if (comp.DispenseOnHitAccumulator >= comp.DispenseOnHitCooldown)
                    {
                        comp.DispenseOnHitAccumulator = 0f;
                        comp.DispenseOnHitCoolingDown = false;
                    }
                }
            }
            var disabled = EntityQueryEnumerator<EmpDisabledComponent, VendingMachineComponent>();
            while (disabled.MoveNext(out var uid, out _, out var comp))
            {
                if (comp.NextEmpEject < _timing.CurTime)
                {
                    EjectRandom(uid, true, false, comp);
                    comp.NextEmpEject += TimeSpan.FromSeconds(5 * comp.EjectDelay);
                }
            }
        }

        public void TryRestockInventory(EntityUid uid, VendingMachineComponent? vendComponent = null)
        {
            if (!Resolve(uid, ref vendComponent))
                return;

            RestockInventoryFromPrototype(uid, vendComponent);

            UpdateVendingMachineInterfaceState(uid, vendComponent);
            TryUpdateVisualState(uid, vendComponent);
        }

        private void OnPriceCalculation(EntityUid uid, VendingMachineRestockComponent component, ref PriceCalculationEvent args)
        {
            List<double> priceSets = new();

            // Find the most expensive inventory and use that as the highest price.
            foreach (var vendingInventory in component.CanRestock)
            {
                double total = 0;

                if (PrototypeManager.TryIndex(vendingInventory, out VendingMachineInventoryPrototype? inventoryPrototype))
                {
                    foreach (var (item, amount) in inventoryPrototype.StartingInventory)
                    {
                        if (PrototypeManager.TryIndex(item, out EntityPrototype? entity))
                            total += _pricing.GetEstimatedPrice(entity) * amount;
                    }
                }

                priceSets.Add(total);
            }

            args.Price += priceSets.Max();
        }

        private void OnEmpPulse(EntityUid uid, VendingMachineComponent component, ref EmpPulseEvent args)
        {
            if (!component.Broken && this.IsPowered(uid, EntityManager))
            {
                args.Affected = true;
                args.Disabled = true;
                component.NextEmpEject = _timing.CurTime;
            }
        }
    }
}
