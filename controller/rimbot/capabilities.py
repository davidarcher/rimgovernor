"""Explicit native write ownership. New operations require a deliberate routing choice."""
OPERATION_DOMAINS = {
    'post_builder_blueprint': ('construction',),
    'construction_place': ('construction',),
    'post_map_building_power': ('construction',),
    'post_order_designate_area': ('construction','growing'),
    'post_things_set_forbidden': ('construction','growing','supply_access'),
    'orders_unforbid_all': ('construction','growing','supply_access'),
    'post_map_zone_growing': ('growing',),
    'zone_growing_cells': ('growing',),
    'post_map_zone_stockpile': ('storage',),
    'post_map_zone_stockpile_update': ('storage',),
    'delete_map_zone_stockpile_delete': ('storage',),
    'post_buildings_bills_add': ('production',),
    'delete_buildings_bills_remove': ('production',),
    'delete_buildings_bill_remove': ('production',),
    'put_buildings_bill_update': ('production',),
    'put_buildings_bill_suspend': ('production',),
    'put_buildings_bill_reorder': ('production',),
    'post_work_settings': ('work_assignment',),
    'post_colonist_work_priority': ('work_assignment',),
    'post_colonists_work_priority': ('work_assignment',),
    'post_colonist_time_assignment': ('work_assignment',),
    'post_pawn_medical_bed_rest': ('care',),
    'post_pawn_medical_tend': ('care',),
    'post_pawn_job': ('security',),
    'post_pawn_edit_status': ('security',),
    'post_jobs_make_equip': ('security',),
    'post_research_stop': ('research',),
    'post_research_target': ('research',),
}
CONTROLLER_OWNED = {'planning_create':'architect', 'planning_remove':'architect'}
WITHHELD = {'post_pawn_edit_apparel':'Drops all equipment immediately through an editor; use normal targeted gear actions.'}

EXECUTION_DOMAINS = {kind:tuple(name for name,owners in OPERATION_DOMAINS.items() if kind in owners)
                     for kind in ('construction','growing','production','storage','work_assignment','supply_access','care','security','research')}
ROLE_SYSTEMS = {
    'Survival': ('care','supply_access'),
    'Infrastructure': ('construction','growing','production','storage'),
    'Security': ('security','care'),
    'Development': ('research',),
    'Workforce': ('work_assignment','security','care'),
}
ROLE_DOMAINS = {role:tuple(dict.fromkeys(name for kind in kinds for name in EXECUTION_DOMAINS[kind]))
                for role,kinds in ROLE_SYSTEMS.items()}


def attach_execution_policy(entry):
    if not entry['write']:return
    name=entry['name']
    entry['execution_domains']=list(OPERATION_DOMAINS.get(name,()))
    entry['controller_owner']=CONTROLLER_OWNED.get(name)
    if name in WITHHELD:
        entry['exposed']=False
        entry['withheld_reason']=WITHHELD[name]
    if entry['exposed'] and not entry['execution_domains'] and not entry['controller_owner']:
        raise ValueError(f'Exposed native write has no execution policy: {name}')
