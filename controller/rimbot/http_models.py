# Generated from Contracts/rimapi.openapi.json. Do not edit.
from __future__ import annotations
from typing import Literal, Union
from pydantic import BaseModel, ConfigDict, Field, JsonValue, RootModel, TypeAdapter

class WireModel(BaseModel):
    model_config = ConfigDict(strict=True, extra='forbid', protected_namespaces=())


class AbilityDefDto(WireModel):
    def_name: Union[str, None] = Field(default=None)
    label: Union[str, None] = Field(default=None)
    description: Union[str, None] = Field(default=None)
    verb_properties: Union[str, None] = Field(default=None)

class AddRelationRequestDto(WireModel):
    pawn1_id: int = Field()
    pawn2_id: int = Field()
    relation_def_name: Union[str, None] = Field(default=None)

class AllDefsRequestDto(WireModel):
    filters: Union[list[str], None] = Field(default=None)

class AnimalDefDto(WireModel):
    def_name: Union[str, None] = Field(default=None)
    label: Union[str, None] = Field(default=None)
    description: Union[str, None] = Field(default=None)
    base_body_size: float = Field()
    base_health_scale: float = Field()
    food_type: Union[str, None] = Field(default=None)
    predator: bool = Field()
    pack_animal: bool = Field()
    petness: float = Field()
    life_stages: Union[list[LifeStageAgeDto], None] = Field(default=None)

class AnimalDto(WireModel):
    id: int = Field()
    name: Union[str, None] = Field(default=None)
    def_: Union[str, None] = Field(default=None, alias='def')
    faction: Union[str, None] = Field(default=None)
    position: Union[PositionDto, None] = Field(default=None)
    trainer: Union[Union[int, None], None] = Field(default=None)
    pregnant: bool = Field()

class ApiResult(WireModel):
    success: bool = Field()
    errors: list[str] = Field()
    warnings: list[str] = Field()
    timestamp: str = Field()

class ApiResult_Anonymousb7f0d20d59(WireModel):
    success: bool = Field()
    errors: list[str] = Field()
    warnings: list[str] = Field()
    timestamp: str = Field()
    data: Union[DocumentationHealthDto, None] = Field(default=None)

class ApiResult_Anonymouse219319f76(WireModel):
    success: bool = Field()
    errors: list[str] = Field()
    warnings: list[str] = Field()
    timestamp: str = Field()
    data: Union[CacheStatusDto, None] = Field(default=None)

class ApiResult_ApiV1PawnDetailedDto(WireModel):
    success: bool = Field()
    errors: list[str] = Field()
    warnings: list[str] = Field()
    timestamp: str = Field()
    data: Union[ApiV1PawnDetailedDto, None] = Field(default=None)

class ApiResult_BillDto(WireModel):
    success: bool = Field()
    errors: list[str] = Field()
    warnings: list[str] = Field()
    timestamp: str = Field()
    data: Union[BillDto, None] = Field(default=None)

class ApiResult_BlueprintDto(WireModel):
    success: bool = Field()
    errors: list[str] = Field()
    warnings: list[str] = Field()
    timestamp: str = Field()
    data: Union[BlueprintDto, None] = Field(default=None)

class ApiResult_BodyPartsDto(WireModel):
    success: bool = Field()
    errors: list[str] = Field()
    warnings: list[str] = Field()
    timestamp: str = Field()
    data: Union[BodyPartsDto, None] = Field(default=None)

class ApiResult_BuildingDto(WireModel):
    success: bool = Field()
    errors: list[str] = Field()
    warnings: list[str] = Field()
    timestamp: str = Field()
    data: Union[BuildingDto, None] = Field(default=None)

class ApiResult_Camera_CameraScreenshotResponseDto(WireModel):
    success: bool = Field()
    errors: list[str] = Field()
    warnings: list[str] = Field()
    timestamp: str = Field()
    data: Union[Camera_CameraScreenshotResponseDto, None] = Field(default=None)

class ApiResult_CaravanPathDto(WireModel):
    success: bool = Field()
    errors: list[str] = Field()
    warnings: list[str] = Field()
    timestamp: str = Field()
    data: Union[CaravanPathDto, None] = Field(default=None)

class ApiResult_CheckZoneResultDto(WireModel):
    success: bool = Field()
    errors: list[str] = Field()
    warnings: list[str] = Field()
    timestamp: str = Field()
    data: Union[CheckZoneResultDto, None] = Field(default=None)

class ApiResult_CoordinatesDto(WireModel):
    success: bool = Field()
    errors: list[str] = Field()
    warnings: list[str] = Field()
    timestamp: str = Field()
    data: Union[CoordinatesDto, None] = Field(default=None)

class ApiResult_Core_ApiDocumentation(WireModel):
    success: bool = Field()
    errors: list[str] = Field()
    warnings: list[str] = Field()
    timestamp: str = Field()
    data: Union[Core_ApiDocumentation, None] = Field(default=None)

class ApiResult_DefsDto(WireModel):
    success: bool = Field()
    errors: list[str] = Field()
    warnings: list[str] = Field()
    timestamp: str = Field()
    data: Union[DefsDto, None] = Field(default=None)

class ApiResult_Dictionary_string__List_ThingDto(WireModel):
    success: bool = Field()
    errors: list[str] = Field()
    warnings: list[str] = Field()
    timestamp: str = Field()
    data: Union[dict[str, list[ThingDto]], None] = Field(default=None)

class ApiResult_EndpointListDto(WireModel):
    success: bool = Field()
    errors: list[str] = Field()
    warnings: list[str] = Field()
    timestamp: str = Field()
    data: Union[EndpointListDto, None] = Field(default=None)

class ApiResult_FactionChangeRelationResponceDto(WireModel):
    success: bool = Field()
    errors: list[str] = Field()
    warnings: list[str] = Field()
    timestamp: str = Field()
    data: Union[FactionChangeRelationResponceDto, None] = Field(default=None)

class ApiResult_FactionDefDto(WireModel):
    success: bool = Field()
    errors: list[str] = Field()
    warnings: list[str] = Field()
    timestamp: str = Field()
    data: Union[FactionDefDto, None] = Field(default=None)

class ApiResult_FactionDto(WireModel):
    success: bool = Field()
    errors: list[str] = Field()
    warnings: list[str] = Field()
    timestamp: str = Field()
    data: Union[FactionDto, None] = Field(default=None)

class ApiResult_FactionIconImageDto(WireModel):
    success: bool = Field()
    errors: list[str] = Field()
    warnings: list[str] = Field()
    timestamp: str = Field()
    data: Union[FactionIconImageDto, None] = Field(default=None)

class ApiResult_FactionRelationDto(WireModel):
    success: bool = Field()
    errors: list[str] = Field()
    warnings: list[str] = Field()
    timestamp: str = Field()
    data: Union[FactionRelationDto, None] = Field(default=None)

class ApiResult_FactionRelationsDto(WireModel):
    success: bool = Field()
    errors: list[str] = Field()
    warnings: list[str] = Field()
    timestamp: str = Field()
    data: Union[FactionRelationsDto, None] = Field(default=None)

class ApiResult_FogGridDto(WireModel):
    success: bool = Field()
    errors: list[str] = Field()
    warnings: list[str] = Field()
    timestamp: str = Field()
    data: Union[FogGridDto, None] = Field(default=None)

class ApiResult_GameSettingsDto(WireModel):
    success: bool = Field()
    errors: list[str] = Field()
    warnings: list[str] = Field()
    timestamp: str = Field()
    data: Union[GameSettingsDto, None] = Field(default=None)

class ApiResult_GameStateDto(WireModel):
    success: bool = Field()
    errors: list[str] = Field()
    warnings: list[str] = Field()
    timestamp: str = Field()
    data: Union[GameStateDto, None] = Field(default=None)

class ApiResult_GrowingZoneDto(WireModel):
    success: bool = Field()
    errors: list[str] = Field()
    warnings: list[str] = Field()
    timestamp: str = Field()
    data: Union[GrowingZoneDto, None] = Field(default=None)

class ApiResult_ImageDto(WireModel):
    success: bool = Field()
    errors: list[str] = Field()
    warnings: list[str] = Field()
    timestamp: str = Field()
    data: Union[ImageDto, None] = Field(default=None)

class ApiResult_IncidentChanceDto(WireModel):
    success: bool = Field()
    errors: list[str] = Field()
    warnings: list[str] = Field()
    timestamp: str = Field()
    data: Union[IncidentChanceDto, None] = Field(default=None)

class ApiResult_IncidentsDto(WireModel):
    success: bool = Field()
    errors: list[str] = Field()
    warnings: list[str] = Field()
    timestamp: str = Field()
    data: Union[IncidentsDto, None] = Field(default=None)

class ApiResult_ItemRecipesDto(WireModel):
    success: bool = Field()
    errors: list[str] = Field()
    warnings: list[str] = Field()
    timestamp: str = Field()
    data: Union[ItemRecipesDto, None] = Field(default=None)

class ApiResult_LearningConceptDto(WireModel):
    success: bool = Field()
    errors: list[str] = Field()
    warnings: list[str] = Field()
    timestamp: str = Field()
    data: Union[LearningConceptDto, None] = Field(default=None)

class ApiResult_List_AnimalDto(WireModel):
    success: bool = Field()
    errors: list[str] = Field()
    warnings: list[str] = Field()
    timestamp: str = Field()
    data: Union[list[AnimalDto], None] = Field(default=None)

class ApiResult_List_ApiV1PawnDetailedDto(WireModel):
    success: bool = Field()
    errors: list[str] = Field()
    warnings: list[str] = Field()
    timestamp: str = Field()
    data: Union[list[ApiV1PawnDetailedDto], None] = Field(default=None)

class ApiResult_List_BillDto(WireModel):
    success: bool = Field()
    errors: list[str] = Field()
    warnings: list[str] = Field()
    timestamp: str = Field()
    data: Union[list[BillDto], None] = Field(default=None)

class ApiResult_List_BuildingDto(WireModel):
    success: bool = Field()
    errors: list[str] = Field()
    warnings: list[str] = Field()
    timestamp: str = Field()
    data: Union[list[BuildingDto], None] = Field(default=None)

class ApiResult_List_CaravanDto(WireModel):
    success: bool = Field()
    errors: list[str] = Field()
    warnings: list[str] = Field()
    timestamp: str = Field()
    data: Union[list[CaravanDto], None] = Field(default=None)

class ApiResult_List_FactionsDto(WireModel):
    success: bool = Field()
    errors: list[str] = Field()
    warnings: list[str] = Field()
    timestamp: str = Field()
    data: Union[list[FactionsDto], None] = Field(default=None)

class ApiResult_List_IncidentWeightDto(WireModel):
    success: bool = Field()
    errors: list[str] = Field()
    warnings: list[str] = Field()
    timestamp: str = Field()
    data: Union[list[IncidentWeightDto], None] = Field(default=None)

class ApiResult_List_InteractionDefDto(WireModel):
    success: bool = Field()
    errors: list[str] = Field()
    warnings: list[str] = Field()
    timestamp: str = Field()
    data: Union[list[InteractionDefDto], None] = Field(default=None)

class ApiResult_List_LearningConceptDto(WireModel):
    success: bool = Field()
    errors: list[str] = Field()
    warnings: list[str] = Field()
    timestamp: str = Field()
    data: Union[list[LearningConceptDto], None] = Field(default=None)

class ApiResult_List_LordDto(WireModel):
    success: bool = Field()
    errors: list[str] = Field()
    warnings: list[str] = Field()
    timestamp: str = Field()
    data: Union[list[LordDto], None] = Field(default=None)

class ApiResult_List_MapDto(WireModel):
    success: bool = Field()
    errors: list[str] = Field()
    warnings: list[str] = Field()
    timestamp: str = Field()
    data: Union[list[MapDto], None] = Field(default=None)

class ApiResult_List_ModInfoDto(WireModel):
    success: bool = Field()
    errors: list[str] = Field()
    warnings: list[str] = Field()
    timestamp: str = Field()
    data: Union[list[ModInfoDto], None] = Field(default=None)

class ApiResult_List_OpenWindowDto(WireModel):
    success: bool = Field()
    errors: list[str] = Field()
    warnings: list[str] = Field()
    timestamp: str = Field()
    data: Union[list[OpenWindowDto], None] = Field(default=None)

class ApiResult_List_OutfitDto(WireModel):
    success: bool = Field()
    errors: list[str] = Field()
    warnings: list[str] = Field()
    timestamp: str = Field()
    data: Union[list[OutfitDto], None] = Field(default=None)

class ApiResult_List_PawnDetailedRequestDto(WireModel):
    success: bool = Field()
    errors: list[str] = Field()
    warnings: list[str] = Field()
    timestamp: str = Field()
    data: Union[list[PawnDetailedRequestDto], None] = Field(default=None)

class ApiResult_List_PawnDto(WireModel):
    success: bool = Field()
    errors: list[str] = Field()
    warnings: list[str] = Field()
    timestamp: str = Field()
    data: Union[list[PawnDto], None] = Field(default=None)

class ApiResult_List_PawnOpinionDto(WireModel):
    success: bool = Field()
    errors: list[str] = Field()
    warnings: list[str] = Field()
    timestamp: str = Field()
    data: Union[list[PawnOpinionDto], None] = Field(default=None)

class ApiResult_List_PawnPositionDto(WireModel):
    success: bool = Field()
    errors: list[str] = Field()
    warnings: list[str] = Field()
    timestamp: str = Field()
    data: Union[list[PawnPositionDto], None] = Field(default=None)

class ApiResult_List_RecipeDto(WireModel):
    success: bool = Field()
    errors: list[str] = Field()
    warnings: list[str] = Field()
    timestamp: str = Field()
    data: Union[list[RecipeDto], None] = Field(default=None)

class ApiResult_List_SettlementDto(WireModel):
    success: bool = Field()
    errors: list[str] = Field()
    warnings: list[str] = Field()
    timestamp: str = Field()
    data: Union[list[SettlementDto], None] = Field(default=None)

class ApiResult_List_SiteDto(WireModel):
    success: bool = Field()
    errors: list[str] = Field()
    warnings: list[str] = Field()
    timestamp: str = Field()
    data: Union[list[SiteDto], None] = Field(default=None)

class ApiResult_List_ThingDto(WireModel):
    success: bool = Field()
    errors: list[str] = Field()
    warnings: list[str] = Field()
    timestamp: str = Field()
    data: Union[list[ThingDto], None] = Field(default=None)

class ApiResult_List_TileDto(WireModel):
    success: bool = Field()
    errors: list[str] = Field()
    warnings: list[str] = Field()
    timestamp: str = Field()
    data: Union[list[TileDto], None] = Field(default=None)

class ApiResult_List_TimeAssignmentDto(WireModel):
    success: bool = Field()
    errors: list[str] = Field()
    warnings: list[str] = Field()
    timestamp: str = Field()
    data: Union[list[TimeAssignmentDto], None] = Field(default=None)

class ApiResult_List_TraderKindDto(WireModel):
    success: bool = Field()
    errors: list[str] = Field()
    warnings: list[str] = Field()
    timestamp: str = Field()
    data: Union[list[TraderKindDto], None] = Field(default=None)

class ApiResult_List_UI_AlertDto(WireModel):
    success: bool = Field()
    errors: list[str] = Field()
    warnings: list[str] = Field()
    timestamp: str = Field()
    data: Union[list[UI_AlertDto], None] = Field(default=None)

class ApiResult_List_WorkTableDto(WireModel):
    success: bool = Field()
    errors: list[str] = Field()
    warnings: list[str] = Field()
    timestamp: str = Field()
    data: Union[list[WorkTableDto], None] = Field(default=None)

class ApiResult_List_string(WireModel):
    success: bool = Field()
    errors: list[str] = Field()
    warnings: list[str] = Field()
    timestamp: str = Field()
    data: Union[list[str], None] = Field(default=None)

class ApiResult_LordCreateDto(WireModel):
    success: bool = Field()
    errors: list[str] = Field()
    warnings: list[str] = Field()
    timestamp: str = Field()
    data: Union[LordCreateDto, None] = Field(default=None)

class ApiResult_MapCreaturesSummaryDto(WireModel):
    success: bool = Field()
    errors: list[str] = Field()
    warnings: list[str] = Field()
    timestamp: str = Field()
    data: Union[MapCreaturesSummaryDto, None] = Field(default=None)

class ApiResult_MapFarmSummaryDto(WireModel):
    success: bool = Field()
    errors: list[str] = Field()
    warnings: list[str] = Field()
    timestamp: str = Field()
    data: Union[MapFarmSummaryDto, None] = Field(default=None)

class ApiResult_MapPowerInfoDto(WireModel):
    success: bool = Field()
    errors: list[str] = Field()
    warnings: list[str] = Field()
    timestamp: str = Field()
    data: Union[MapPowerInfoDto, None] = Field(default=None)

class ApiResult_MapRoomsDto(WireModel):
    success: bool = Field()
    errors: list[str] = Field()
    warnings: list[str] = Field()
    timestamp: str = Field()
    data: Union[MapRoomsDto, None] = Field(default=None)

class ApiResult_MapTerrainDto(WireModel):
    success: bool = Field()
    errors: list[str] = Field()
    warnings: list[str] = Field()
    timestamp: str = Field()
    data: Union[MapTerrainDto, None] = Field(default=None)

class ApiResult_MapTimeDto(WireModel):
    success: bool = Field()
    errors: list[str] = Field()
    warnings: list[str] = Field()
    timestamp: str = Field()
    data: Union[MapTimeDto, None] = Field(default=None)

class ApiResult_MapWeatherDto(WireModel):
    success: bool = Field()
    errors: list[str] = Field()
    warnings: list[str] = Field()
    timestamp: str = Field()
    data: Union[MapWeatherDto, None] = Field(default=None)

class ApiResult_MapZonesDto(WireModel):
    success: bool = Field()
    errors: list[str] = Field()
    warnings: list[str] = Field()
    timestamp: str = Field()
    data: Union[MapZonesDto, None] = Field(default=None)

class ApiResult_Map_OreDataDto(WireModel):
    success: bool = Field()
    errors: list[str] = Field()
    warnings: list[str] = Field()
    timestamp: str = Field()
    data: Union[Map_OreDataDto, None] = Field(default=None)

class ApiResult_MaterialsAtlasList(WireModel):
    success: bool = Field()
    errors: list[str] = Field()
    warnings: list[str] = Field()
    timestamp: str = Field()
    data: Union[MaterialsAtlasList, None] = Field(default=None)

class ApiResult_ModInfoDto(WireModel):
    success: bool = Field()
    errors: list[str] = Field()
    warnings: list[str] = Field()
    timestamp: str = Field()
    data: Union[ModInfoDto, None] = Field(default=None)

class ApiResult_OpinionAboutPawnDto(WireModel):
    success: bool = Field()
    errors: list[str] = Field()
    warnings: list[str] = Field()
    timestamp: str = Field()
    data: Union[OpinionAboutPawnDto, None] = Field(default=None)

class ApiResult_PawnDetailedDto(WireModel):
    success: bool = Field()
    errors: list[str] = Field()
    warnings: list[str] = Field()
    timestamp: str = Field()
    data: Union[PawnDetailedDto, None] = Field(default=None)

class ApiResult_PawnDetailedRequestDto(WireModel):
    success: bool = Field()
    errors: list[str] = Field()
    warnings: list[str] = Field()
    timestamp: str = Field()
    data: Union[PawnDetailedRequestDto, None] = Field(default=None)

class ApiResult_PawnDto(WireModel):
    success: bool = Field()
    errors: list[str] = Field()
    warnings: list[str] = Field()
    timestamp: str = Field()
    data: Union[PawnDto, None] = Field(default=None)

class ApiResult_PawnInteractionLogDto(WireModel):
    success: bool = Field()
    errors: list[str] = Field()
    warnings: list[str] = Field()
    timestamp: str = Field()
    data: Union[PawnInteractionLogDto, None] = Field(default=None)

class ApiResult_PawnInteractionStatusDto(WireModel):
    success: bool = Field()
    errors: list[str] = Field()
    warnings: list[str] = Field()
    timestamp: str = Field()
    data: Union[PawnInteractionStatusDto, None] = Field(default=None)

class ApiResult_PawnInventoryDto(WireModel):
    success: bool = Field()
    errors: list[str] = Field()
    warnings: list[str] = Field()
    timestamp: str = Field()
    data: Union[PawnInventoryDto, None] = Field(default=None)

class ApiResult_PawnRelationsDto(WireModel):
    success: bool = Field()
    errors: list[str] = Field()
    warnings: list[str] = Field()
    timestamp: str = Field()
    data: Union[PawnRelationsDto, None] = Field(default=None)

class ApiResult_PawnSpawnDto(WireModel):
    success: bool = Field()
    errors: list[str] = Field()
    warnings: list[str] = Field()
    timestamp: str = Field()
    data: Union[PawnSpawnDto, None] = Field(default=None)

class ApiResult_QuestsDto(WireModel):
    success: bool = Field()
    errors: list[str] = Field()
    warnings: list[str] = Field()
    timestamp: str = Field()
    data: Union[QuestsDto, None] = Field(default=None)

class ApiResult_ResearchFinishedDto(WireModel):
    success: bool = Field()
    errors: list[str] = Field()
    warnings: list[str] = Field()
    timestamp: str = Field()
    data: Union[ResearchFinishedDto, None] = Field(default=None)

class ApiResult_ResearchProjectDto(WireModel):
    success: bool = Field()
    errors: list[str] = Field()
    warnings: list[str] = Field()
    timestamp: str = Field()
    data: Union[ResearchProjectDto, None] = Field(default=None)

class ApiResult_ResearchSummaryDto(WireModel):
    success: bool = Field()
    errors: list[str] = Field()
    warnings: list[str] = Field()
    timestamp: str = Field()
    data: Union[ResearchSummaryDto, None] = Field(default=None)

class ApiResult_ResearchTreeDto(WireModel):
    success: bool = Field()
    errors: list[str] = Field()
    warnings: list[str] = Field()
    timestamp: str = Field()
    data: Union[ResearchTreeDto, None] = Field(default=None)

class ApiResult_ResourcesSummaryDto(WireModel):
    success: bool = Field()
    errors: list[str] = Field()
    warnings: list[str] = Field()
    timestamp: str = Field()
    data: Union[ResourcesSummaryDto, None] = Field(default=None)

class ApiResult_ServerCacheResponseDto(WireModel):
    success: bool = Field()
    errors: list[str] = Field()
    warnings: list[str] = Field()
    timestamp: str = Field()
    data: Union[ServerCacheResponseDto, None] = Field(default=None)

class ApiResult_StockpileResponseDto(WireModel):
    success: bool = Field()
    errors: list[str] = Field()
    warnings: list[str] = Field()
    timestamp: str = Field()
    data: Union[StockpileResponseDto, None] = Field(default=None)

class ApiResult_StoragesSummaryDto(WireModel):
    success: bool = Field()
    errors: list[str] = Field()
    warnings: list[str] = Field()
    timestamp: str = Field()
    data: Union[StoragesSummaryDto, None] = Field(default=None)

class ApiResult_StreamStatusDto(WireModel):
    success: bool = Field()
    errors: list[str] = Field()
    warnings: list[str] = Field()
    timestamp: str = Field()
    data: Union[StreamStatusDto, None] = Field(default=None)

class ApiResult_ThingSourcesDto(WireModel):
    success: bool = Field()
    errors: list[str] = Field()
    warnings: list[str] = Field()
    timestamp: str = Field()
    data: Union[ThingSourcesDto, None] = Field(default=None)

class ApiResult_TileDetailsDto(WireModel):
    success: bool = Field()
    errors: list[str] = Field()
    warnings: list[str] = Field()
    timestamp: str = Field()
    data: Union[TileDetailsDto, None] = Field(default=None)

class ApiResult_TileDto(WireModel):
    success: bool = Field()
    errors: list[str] = Field()
    warnings: list[str] = Field()
    timestamp: str = Field()
    data: Union[TileDto, None] = Field(default=None)

class ApiResult_TraitDefDto(WireModel):
    success: bool = Field()
    errors: list[str] = Field()
    warnings: list[str] = Field()
    timestamp: str = Field()
    data: Union[TraitDefDto, None] = Field(default=None)

class ApiResult_VersionDto(WireModel):
    success: bool = Field()
    errors: list[str] = Field()
    warnings: list[str] = Field()
    timestamp: str = Field()
    data: Union[VersionDto, None] = Field(default=None)

class ApiResult_WindowCloseResultDto(WireModel):
    success: bool = Field()
    errors: list[str] = Field()
    warnings: list[str] = Field()
    timestamp: str = Field()
    data: Union[WindowCloseResultDto, None] = Field(default=None)

class ApiResult_WorkListDto(WireModel):
    success: bool = Field()
    errors: list[str] = Field()
    warnings: list[str] = Field()
    timestamp: str = Field()
    data: Union[WorkListDto, None] = Field(default=None)

class ApiResult_bool(WireModel):
    success: bool = Field()
    errors: list[str] = Field()
    warnings: list[str] = Field()
    timestamp: str = Field()
    data: Union[bool, None] = Field(default=None)

class ApiResult_string(WireModel):
    success: bool = Field()
    errors: list[str] = Field()
    warnings: list[str] = Field()
    timestamp: str = Field()
    data: Union[str, None] = Field(default=None)

class ApiV1PawnDetailedDto(WireModel):
    colonist: Union[PawnDto, None] = Field(default=None)
    body_size: float = Field()
    sleep: Union[Union[float, None], None] = Field(default=None)
    comfort: Union[Union[float, None], None] = Field(default=None)
    beauty: Union[Union[float, None], None] = Field(default=None)
    joy: Union[Union[float, None], None] = Field(default=None)
    energy: Union[Union[float, None], None] = Field(default=None)
    drugs_desire: Union[Union[float, None], None] = Field(default=None)
    surrounding_beauty: Union[Union[float, None], None] = Field(default=None)
    fresh_air: Union[Union[float, None], None] = Field(default=None)
    colonist_work_info: Union[WorkInfoDto, None] = Field(default=None)
    policies_info: Union[PoliciesInfoDto, None] = Field(default=None)
    colonist_medical_info: Union[MedicalInfoDto, None] = Field(default=None)
    social_info: Union[SocialInfoDto, None] = Field(default=None)

class BillDto(WireModel):
    load_id: int = Field()
    recipe_def_name: Union[str, None] = Field(default=None)
    recipe_label: Union[str, None] = Field(default=None)
    suspended: bool = Field()
    paused: bool = Field()
    repeat_mode: Union[str, None] = Field(default=None)
    repeat_count: int = Field()
    target_count: int = Field()
    store_mode: Union[str, None] = Field(default=None)
    pause_when_satisfied: bool = Field()
    unpause_when_you_have: int = Field()
    include_equipped: bool = Field()
    include_tainted: bool = Field()
    hp_range: Union[FloatRange, None] = Field(default=None)
    quality_range: Union[IntRangeDto, None] = Field(default=None)
    limit_to_allowed_stuff: bool = Field()
    ingredient_search_radius: float = Field()
    allowed_skill_range: Union[IntRangeDto, None] = Field(default=None)
    pawn_restriction_id: Union[Union[int, None], None] = Field(default=None)
    player_custom_name: Union[str, None] = Field(default=None)
    slot_group_id: Union[Union[int, None], None] = Field(default=None)
    allowed_materials: Union[list[str], None] = Field(default=None)
    available_materials: Union[list[str], None] = Field(default=None)

class BillRecipeIngredientDto(WireModel):
    filter_label: Union[str, None] = Field(default=None)
    count: float = Field()

class BillReorderRequest(WireModel):
    offset: int = Field()

class BillSuspendRequest(WireModel):
    suspended: bool = Field()

class BiomeDefDto(WireModel):
    def_name: Union[str, None] = Field(default=None)
    label: Union[str, None] = Field(default=None)
    description: Union[str, None] = Field(default=None)
    animal_density: float = Field()
    plant_density: float = Field()
    disease_mtb_days: float = Field()
    foraged_food: Union[str, None] = Field(default=None)
    movement_difficulty: float = Field()
    all_wild_plants: Union[list[str], None] = Field(default=None)

class BlueprintDto(WireModel):
    width: int = Field()
    height: int = Field()
    floors: Union[list[SavedTerrainDto], None] = Field(default=None)
    buildings: Union[list[SavedBuildingDto], None] = Field(default=None)

class BodyDefDto(WireModel):
    def_name: Union[str, None] = Field(default=None)
    label: Union[str, None] = Field(default=None)
    core_part: Union[str, None] = Field(default=None)
    parts: Union[list[str], None] = Field(default=None)

class BodyPartDefDto(WireModel):
    def_name: Union[str, None] = Field(default=None)
    label: Union[str, None] = Field(default=None)
    bleed_rate: float = Field()
    hit_points: float = Field()
    permanent_injury_chance_factor: float = Field()

class BodyPartsDto(WireModel):
    body_image: Union[str, None] = Field(default=None)
    body_color: Union[str, None] = Field(default=None)
    head_image: Union[str, None] = Field(default=None)
    head_color: Union[str, None] = Field(default=None)

class BuildingDto(WireModel):
    id: int = Field()
    def_: Union[str, None] = Field(default=None, alias='def')
    label: Union[str, None] = Field(default=None)
    position: Union[PositionDto, None] = Field(default=None)
    rotation: int = Field()
    size: Union[PositionDto, None] = Field(default=None)
    type: Union[str, None] = Field(default=None)

class CacheStatusDto(WireModel):
    enabled: bool = Field()
    statistics: Union[Core_CacheStatistics, None] = Field(default=None)

class Camera_CameraScreenshotRequestDto(WireModel):
    format: Union[str, None] = Field(default=None)
    quality: int = Field()
    width: Union[Union[int, None], None] = Field(default=None)
    height: Union[Union[int, None], None] = Field(default=None)
    hide_ui: bool = Field()

class Camera_CameraScreenshotResponseDto(WireModel):
    image: Union[Camera_ImageData, None] = Field(default=None)
    metadata: Union[Camera_ImageMetadata, None] = Field(default=None)
    game_context: Union[Camera_GameContext, None] = Field(default=None)

class Camera_GameContext(WireModel):
    current_tick: int = Field()

class Camera_ImageData(WireModel):
    data_uri: Union[str, None] = Field(default=None)

class Camera_ImageMetadata(WireModel):
    format: Union[str, None] = Field(default=None)
    width: int = Field()
    height: int = Field()
    size_bytes: int = Field()

class Camera_NativeScreenshotRequestDto(WireModel):
    file_name: Union[str, None] = Field(default=None)
    center_x: Union[Union[float, None], None] = Field(default=None)
    center_z: Union[Union[float, None], None] = Field(default=None)
    zoom_level: Union[Union[float, None], None] = Field(default=None)
    hide_u_i: bool = Field()

class CaravanDto(WireModel):
    id: int = Field()
    name: Union[str, None] = Field(default=None)
    is_player_controlled: bool = Field()
    position: Union[PositionDto, None] = Field(default=None)
    tile: int = Field()
    pawns: Union[list[PawnDto], None] = Field(default=None)
    items: Union[list[ThingDto], None] = Field(default=None)
    mass_usage: float = Field()
    mass_capacity: float = Field()
    forageability: Union[str, None] = Field(default=None)
    visibility: Union[str, None] = Field(default=None)
    days_to_arrive: float = Field()

class CaravanPathDto(WireModel):
    id: int = Field()
    moving: bool = Field()
    current_tile: int = Field()
    next_tile: int = Field()
    progress: float = Field()
    destination_tile: int = Field()
    path: Union[list[int], None] = Field(default=None)

class CheckZoneIssueDto(WireModel):
    x: int = Field()
    z: int = Field()
    def_name: Union[str, None] = Field(default=None)
    label: Union[str, None] = Field(default=None)
    zone_type: Union[str, None] = Field(default=None)

class CheckZoneIssuesDto(WireModel):
    terrain: Union[list[CheckZoneIssueDto], None] = Field(default=None)
    ores: Union[list[CheckZoneIssueDto], None] = Field(default=None)
    buildings: Union[list[CheckZoneIssueDto], None] = Field(default=None)
    zones: Union[list[CheckZoneIssueDto], None] = Field(default=None)

class CheckZoneRequestDto(WireModel):
    map_id: int = Field()
    point_a: Union[PositionDto, None] = Field(default=None)
    point_b: Union[PositionDto, None] = Field(default=None)

class CheckZoneResultDto(WireModel):
    can_build: bool = Field()
    issues: Union[CheckZoneIssuesDto, None] = Field(default=None)

class ColonistsWorkPrioritiesRequestDto(WireModel):
    priorities: Union[list[WorkPriorityRequestDto], None] = Field(default=None)

class ConfigureModsRequestDto(WireModel):
    package_ids: Union[list[str], None] = Field(default=None)
    restart_game: bool = Field()

class CoordinatesDto(WireModel):
    lat: float = Field()
    lon: float = Field()

class CopyAreaRequestDto(WireModel):
    map_id: int = Field()
    point_a: Union[PositionDto, None] = Field(default=None)
    point_b: Union[PositionDto, None] = Field(default=None)

class Core_ApiDocumentation(WireModel):
    generated_at: str = Field()
    base_url: Union[str, None] = Field(default=None)
    version: Union[str, None] = Field(default=None)
    sections: Union[list[Core_DocumentationSection], None] = Field(default=None)

class Core_CacheEntryDetail(WireModel):
    key: Union[str, None] = Field(default=None)
    type: Union[str, None] = Field(default=None)
    created: str = Field()
    last_accessed: str = Field()
    absolute_expiration: Union[Union[str, None], None] = Field(default=None)
    remaining_seconds: float = Field()
    hits: int = Field()
    estimated_size: int = Field()
    priority: Union[str, None] = Field(default=None)

class Core_CacheStatistics(WireModel):
    total_entries: int = Field()
    hits: int = Field()
    misses: int = Field()
    memory_usage_bytes: int = Field()
    last_cleanup: str = Field()
    hit_ratio: float = Field()
    compiled_delegate_count: int = Field()
    compiled_delegate_hits: int = Field()
    compiled_delegate_misses: int = Field()
    entries: Union[list[Core_CacheEntryDetail], None] = Field(default=None)
    recent_activity: Union[list[Core_RecentHitDetail], None] = Field(default=None)

class Core_DocumentationSection(WireModel):
    name: Union[str, None] = Field(default=None)
    description: Union[str, None] = Field(default=None)
    endpoints: Union[list[Core_DocumentedEndpoint], None] = Field(default=None)

class Core_DocumentedEndpoint(WireModel):
    method: Union[str, None] = Field(default=None)
    path: Union[str, None] = Field(default=None)
    description: Union[str, None] = Field(default=None)
    github_link_title: Union[str, None] = Field(default=None)
    github_link: Union[str, None] = Field(default=None)
    category: Union[str, None] = Field(default=None)
    notes: Union[str, None] = Field(default=None)
    request_example: Union[str, None] = Field(default=None)
    response_example: Union[str, None] = Field(default=None)
    parameters: Union[list[Core_EndpointParameter], None] = Field(default=None)
    controller: Union[str, None] = Field(default=None)
    action: Union[str, None] = Field(default=None)
    requires_authentication: bool = Field()
    tags: Union[list[str], None] = Field(default=None)
    deprecation_notice: Union[str, None] = Field(default=None)
    since_version: Union[str, None] = Field(default=None)

class Core_EndpointParameter(WireModel):
    name: Union[str, None] = Field(default=None)
    type: Union[str, None] = Field(default=None)
    source: Union[str, None] = Field(default=None)
    required: bool = Field()
    description: Union[str, None] = Field(default=None)
    example: Union[str, None] = Field(default=None)
    default_value: Union[str, None] = Field(default=None)
    allowed_values: Union[list[str], None] = Field(default=None)
    validation: Union[Core_ParameterValidation, None] = Field(default=None)

class Core_ParameterValidation(WireModel):
    min_length: Union[Union[int, None], None] = Field(default=None)
    max_length: Union[Union[int, None], None] = Field(default=None)
    pattern: Union[str, None] = Field(default=None)
    minimum: Union[JsonValue, None] = Field(default=None)
    maximum: Union[JsonValue, None] = Field(default=None)

class Core_RecentHitDetail(WireModel):
    key: Union[str, None] = Field(default=None)
    timestamp: str = Field()
    is_hit: bool = Field()

class CreateBillRequest(WireModel):
    recipe_def_name: Union[str, None] = Field(default=None)
    repeat_mode: Union[str, None] = Field(default=None)
    repeat_count: Union[Union[int, None], None] = Field(default=None)
    target_count: Union[Union[int, None], None] = Field(default=None)
    store_mode: Union[str, None] = Field(default=None)
    suspended: Union[Union[bool, None], None] = Field(default=None)
    pause_when_satisfied: Union[Union[bool, None], None] = Field(default=None)
    unpause_when_you_have: Union[Union[int, None], None] = Field(default=None)
    include_equipped: Union[Union[bool, None], None] = Field(default=None)
    include_tainted: Union[Union[bool, None], None] = Field(default=None)
    hp_range: Union[FloatRange, None] = Field(default=None)
    quality_range: Union[IntRangeDto, None] = Field(default=None)
    limit_to_allowed_stuff: Union[Union[bool, None], None] = Field(default=None)
    ingredient_search_radius: Union[Union[float, None], None] = Field(default=None)
    allowed_skill_range: Union[IntRangeDto, None] = Field(default=None)
    pawn_restriction_id: Union[Union[int, None], None] = Field(default=None)
    player_custom_name: Union[str, None] = Field(default=None)
    allowed_materials: Union[list[str], None] = Field(default=None)

class CreateGrowingZoneRequestDto(WireModel):
    map_id: int = Field()
    plant_def: Union[str, None] = Field(default=None)
    point_a: Union[PositionDto, None] = Field(default=None)
    point_b: Union[PositionDto, None] = Field(default=None)

class CreateStockpileRequestDto(WireModel):
    map_id: int = Field()
    point_a: Union[PositionDto, None] = Field(default=None)
    point_b: Union[PositionDto, None] = Field(default=None)
    name: Union[str, None] = Field(default=None)
    priority: Union[Union[int, None], None] = Field(default=None)
    allowed_item_defs: Union[list[str], None] = Field(default=None)
    allowed_item_categories: Union[list[str], None] = Field(default=None)
    min_hit_points_percent: Union[Union[float, None], None] = Field(default=None)
    max_hit_points_percent: Union[Union[float, None], None] = Field(default=None)
    min_quality: Union[str, None] = Field(default=None)
    max_quality: Union[str, None] = Field(default=None)

class CriticalResourcesDto(WireModel):
    food_summary: Union[ResourcesFoodSummaryDto, None] = Field(default=None)
    medicine_total: int = Field()
    weapon_count: int = Field()
    weapon_value: float = Field()

class CropTypeDto(WireModel):
    plant_def_name: Union[str, None] = Field(default=None)
    plant_label: Union[str, None] = Field(default=None)
    plant_category: Union[str, None] = Field(default=None)
    total_plants: int = Field()
    harvestable_plants: int = Field()
    expected_yield: int = Field()
    infected_count: int = Field()
    growth_progress_average: float = Field()
    days_until_harvest: float = Field()
    is_fully_grown: bool = Field()
    is_harvestable: bool = Field()
    zone_id: int = Field()

class DebugConsoleRequest(WireModel):
    action: Union[str, None] = Field(default=None)
    message: Union[str, None] = Field(default=None)

class DefsDto(WireModel):
    things_defs: Union[list[ThingDefDto], None] = Field(default=None)
    incidents_defs: Union[list[IncidentDefDto], None] = Field(default=None)
    conditions_defs: Union[list[GameConditionDefDto], None] = Field(default=None)
    pawn_kind_defs: Union[list[PawnKindDefDto], None] = Field(default=None)
    trait_defs: Union[list[TraitDefDto], None] = Field(default=None)
    research_defs: Union[list[ResearchProjectDefDto], None] = Field(default=None)
    hediff_defs: Union[list[HediffDefDto], None] = Field(default=None)
    skill_defs: Union[list[SkillDefDto], None] = Field(default=None)
    work_type_defs: Union[list[WorkTypeDefDto], None] = Field(default=None)
    need_defs: Union[list[NeedDefDto], None] = Field(default=None)
    thought_defs: Union[list[ThoughtDefDto], None] = Field(default=None)
    stat_defs: Union[list[StatDefDto], None] = Field(default=None)
    world_object_defs: Union[list[WorldObjectDefDto], None] = Field(default=None)
    biome_defs: Union[list[BiomeDefDto], None] = Field(default=None)
    terrain_defs: Union[list[TerrainDefDto], None] = Field(default=None)
    recipe_defs: Union[list[RecipeDefDto], None] = Field(default=None)
    body_defs: Union[list[BodyDefDto], None] = Field(default=None)
    body_part_defs: Union[list[BodyPartDefDto], None] = Field(default=None)
    faction_defs: Union[list[FactionDefDto], None] = Field(default=None)
    sound_defs: Union[list[SoundDefDto], None] = Field(default=None)
    designation_category_defs: Union[list[DesignationCategoryDefDto], None] = Field(default=None)
    joy_kind_defs: Union[list[JoyKindDefDto], None] = Field(default=None)
    meme_defs: Union[list[MemeDefDto], None] = Field(default=None)
    precept_defs: Union[list[PreceptDefDto], None] = Field(default=None)
    ability_defs: Union[list[AbilityDefDto], None] = Field(default=None)
    gene_defs: Union[list[GeneDefDto], None] = Field(default=None)
    weather_defs: Union[list[WeatherDefDto], None] = Field(default=None)
    room_role_defs: Union[list[RoomRoleDefDto], None] = Field(default=None)
    room_stat_defs: Union[list[RoomStatDefDto], None] = Field(default=None)
    mental_state_defs: Union[list[MentalStateDefDto], None] = Field(default=None)
    drug_policy_defs: Union[list[DrugPolicyDefDto], None] = Field(default=None)
    plant_defs: Union[list[PlantDefDto], None] = Field(default=None)
    animal_defs: Union[list[AnimalDefDto], None] = Field(default=None)
    storyteller_defs: Union[list[StorytellerDefDto], None] = Field(default=None)
    difficulty_defs: Union[list[DifficultyDefDto], None] = Field(default=None)
    job_defs: Union[list[JobDefDto], None] = Field(default=None)

class DesignateRequestDto(WireModel):
    map_id: int = Field()
    type: Union[str, None] = Field(default=None)
    point_a: Union[PositionDto, None] = Field(default=None)
    point_b: Union[PositionDto, None] = Field(default=None)

class DesignationCategoryDefDto(WireModel):
    def_name: Union[str, None] = Field(default=None)
    label: Union[str, None] = Field(default=None)
    description: Union[str, None] = Field(default=None)

class DestroyRectRequestDto(WireModel):
    map_id: int = Field()
    point_a: Union[PositionDto, None] = Field(default=None)
    point_b: Union[PositionDto, None] = Field(default=None)

class DialogOptionDto(WireModel):
    label: Union[str, None] = Field(default=None)
    action_id: Union[str, None] = Field(default=None)
    resolve_tree: bool = Field()

class DifficultyDefDto(WireModel):
    def_name: Union[str, None] = Field(default=None)

class DocumentationHealthDto(WireModel):
    status: Union[str, None] = Field(default=None)
    generated_at: str = Field()
    total_endpoints: int = Field()
    total_extensions: int = Field()
    sections: Union[list[str], None] = Field(default=None)

class DrugPolicyDefDto(WireModel):
    def_name: Union[str, None] = Field(default=None)
    label: Union[str, None] = Field(default=None)
    entries: Union[list[DrugPolicyEntryDto], None] = Field(default=None)

class DrugPolicyEntryDto(WireModel):
    drug: Union[str, None] = Field(default=None)
    allowed_for_addiction: bool = Field()
    allowed_for_joy: bool = Field()
    allow_scheduled: bool = Field()
    days_frequency: float = Field()
    only_if_mood_below: float = Field()
    only_if_joy_below: float = Field()

class EndpointDto(WireModel):
    method: Union[str, None] = Field(default=None)
    path: Union[str, None] = Field(default=None)
    description: Union[str, None] = Field(default=None)
    category: Union[str, None] = Field(default=None)
    tags: Union[list[str], None] = Field(default=None)
    is_deprecated: bool = Field()

class EndpointListDto(WireModel):
    endpoints: Union[list[EndpointDto], None] = Field(default=None)

class FactionChangeRelationRequestDto(WireModel):
    id: int = Field()
    other_id: int = Field()
    value: int = Field()
    send_message: bool = Field()
    can_send_hostility_letter: bool = Field()

class FactionChangeRelationResponceDto(WireModel):
    id: int = Field()
    other_id: int = Field()
    value: int = Field()

class FactionDefDto(WireModel):
    def_name: Union[str, None] = Field(default=None)
    pawn_singular: Union[str, None] = Field(default=None)
    pawns_plural: Union[str, None] = Field(default=None)
    leader_title: Union[str, None] = Field(default=None)
    leader_title_female: Union[str, None] = Field(default=None)
    category_tag: Union[str, None] = Field(default=None)
    hostile_to_factionless_humanlikes: bool = Field()
    starting_count_at_world_creation: Union[str, None] = Field(default=None)
    permanent_enemy: bool = Field()
    natural_enemy: bool = Field()
    classic_ideo: bool = Field()
    hidden_ideo: bool = Field()
    ideo_name: Union[str, None] = Field(default=None)
    can_siege: bool = Field()
    can_stage_attacks: bool = Field()
    can_use_avoid_grid: bool = Field()
    can_psychic_ritual_siege: bool = Field()
    earliest_raid_days: float = Field()

class FactionDto(WireModel):
    load_id: int = Field()
    def_name: Union[str, None] = Field(default=None)
    name: Union[str, None] = Field(default=None)
    is_player: bool = Field()
    leader_title: Union[str, None] = Field(default=None)
    leader_id: int = Field()

class FactionIconImageDto(WireModel):
    image: Union[ImageDto, None] = Field(default=None)
    color: Union[str, None] = Field(default=None)

class FactionRelationDto(WireModel):
    goodwill: int = Field()
    relation_kind: Union[str, None] = Field(default=None)

class FactionRelationsDto(WireModel):
    id: int = Field()
    relations: Union[dict[str, FactionRelationDto], None] = Field(default=None)

class FactionsDto(WireModel):
    load_id: int = Field()
    def_name: Union[str, None] = Field(default=None)
    name: Union[str, None] = Field(default=None)
    is_player: bool = Field()
    relation: Union[str, None] = Field(default=None)
    goodwill: int = Field()

class FloatRange(WireModel):
    min: float = Field()
    max: float = Field()

class FogGridDto(WireModel):
    map_id: int = Field()
    width: int = Field()
    height: int = Field()
    fog_data: Union[str, None] = Field(default=None)

class ForceInteractionRequestDto(WireModel):
    initiator_id: int = Field()
    recipient_id: int = Field()
    interaction_def_name: Union[str, None] = Field(default=None)

class GameConditionDefDto(WireModel):
    def_name: Union[str, None] = Field(default=None)
    label: Union[str, None] = Field(default=None)
    description: Union[str, None] = Field(default=None)
    letter_text: Union[str, None] = Field(default=None)
    can_be_permanent: bool = Field()
    temperature_offset: float = Field()
    sky_target: float = Field()
    sky_target_lerp_factor: float = Field()

class GameLoadRequestDto(WireModel):
    file_name: Union[str, None] = Field(default=None)
    check_version: bool = Field()
    skip_mod_mismatch: bool = Field()

class GameSaveRequestDto(WireModel):
    file_name: Union[str, None] = Field(default=None)

class GameSettingsDto(WireModel):
    language: Union[str, None] = Field(default=None)
    run_in_background: bool = Field()
    development_mode: bool = Field()
    log_verbose: bool = Field()
    temperature_mode: Union[str, None] = Field(default=None)
    autosave_interval: bool = Field()
    resolution: Union[str, None] = Field(default=None)
    fullscreen: bool = Field()
    user_interface_scale: float = Field()
    custom_cursor_enabled: bool = Field()
    hats_only_on_map: bool = Field()
    plant_wind_sway: bool = Field()
    max_number_of_player_settlements: int = Field()
    volume_master: float = Field()
    volume_game: float = Field()
    volume_music: float = Field()
    volume_ambient: float = Field()
    pause_on_load: bool = Field()
    pause_on_urgent_letter: Union[str, None] = Field(default=None)
    automatic_pause_on_letter: bool = Field()
    edge_screen_scroll: bool = Field()
    map_drag_sensitivity: float = Field()
    zoom_to_mouse: bool = Field()
    show_realtime_clock: bool = Field()
    resource_readout_categorized: bool = Field()
    show_animal_names: bool = Field()
    reset_mods_config_on_crash: bool = Field()
    texture_compression: int = Field()

class GameStateDto(WireModel):
    session_id: Union[str, None] = Field(default=None)
    game_tick: int = Field()
    colony_wealth: float = Field()
    colonist_count: int = Field()
    storyteller: Union[str, None] = Field(default=None)
    is_paused: bool = Field()
    program_state: Union[str, None] = Field(default=None)
    current_view: Union[str, None] = Field(default=None)
    is_settings_open: bool = Field()
    is_mod_settings_open: bool = Field()
    map_count: int = Field()

class GeneDefDto(WireModel):
    def_name: Union[str, None] = Field(default=None)
    label: Union[str, None] = Field(default=None)
    description: Union[str, None] = Field(default=None)
    biostat_cpx: int = Field()
    biostat_met: int = Field()
    display_category: Union[str, None] = Field(default=None)
    min_age_active: float = Field()

class GrowingZoneDto(WireModel):
    zone: Union[ZoneDto, None] = Field(default=None)
    plant_def_name: Union[str, None] = Field(default=None)
    plant_count: int = Field()
    def_expected_yield: int = Field()
    expected_yield: int = Field()
    infected_count: int = Field()
    growth_progress: float = Field()
    is_sowing: bool = Field()
    soil_type: Union[str, None] = Field(default=None)
    fertility: float = Field()
    has_dying: bool = Field()
    has_dying_from_pollution: bool = Field()
    has_dying_from_no_pollution: bool = Field()

class HediffDefDto(WireModel):
    def_name: Union[str, None] = Field(default=None)
    label: Union[str, None] = Field(default=None)
    description: Union[str, None] = Field(default=None)
    hediff_class: Union[str, None] = Field(default=None)
    is_addiction: bool = Field()
    makes_sick_thought: bool = Field()
    severity_per_day: float = Field()
    max_severity: float = Field()
    tendable: bool = Field()
    is_bad: bool = Field()
    stages: Union[list[HediffStageDto], None] = Field(default=None)
    comp_props: Union[str, None] = Field(default=None)

class HediffDto(WireModel):
    load_id: int = Field()
    def_name: Union[str, None] = Field(default=None)
    label: Union[str, None] = Field(default=None)
    label_cap: Union[str, None] = Field(default=None)
    label_in_brackets: Union[str, None] = Field(default=None)
    severity: float = Field()
    severity_label: Union[str, None] = Field(default=None)
    cur_stage_index: int = Field()
    cur_stage_label: Union[str, None] = Field(default=None)
    part_label: Union[str, None] = Field(default=None)
    part_def_name: Union[str, None] = Field(default=None)
    age_ticks: int = Field()
    age_string: Union[str, None] = Field(default=None)
    visible: bool = Field()
    is_permanent: bool = Field()
    is_tended: bool = Field()
    tendable_now: bool = Field()
    bleeding: bool = Field()
    bleed_rate: float = Field()
    is_lethal: bool = Field()
    is_currently_life_threatening: bool = Field()
    can_ever_kill: bool = Field()
    source_def_name: Union[str, None] = Field(default=None)
    source_label: Union[str, None] = Field(default=None)
    source_body_part_group_def_name: Union[str, None] = Field(default=None)
    source_hediff_def_name: Union[str, None] = Field(default=None)
    combat_log_text: Union[str, None] = Field(default=None)
    tip_string_extra: Union[str, None] = Field(default=None)
    pain_factor: float = Field()
    pain_offset: float = Field()

class HediffStageDto(WireModel):
    min_severity: float = Field()
    label: Union[str, None] = Field(default=None)
    become_immune_to: Union[list[str], None] = Field(default=None)
    death_mtb_days: float = Field()
    pain_factor: float = Field()
    forget_memory_thought_mtb_days: float = Field()
    vomit_mtb_days: float = Field()
    mental_break_mtb_days: float = Field()

class ImageDto(WireModel):
    result: Union[str, None] = Field(default=None)
    image_base64: Union[str, None] = Field(default=None)

class ImageUploadRequest(WireModel):
    name: Union[str, None] = Field(default=None)
    image: Union[str, None] = Field(default=None)
    direction: Union[str, None] = Field(default=None)
    offset: Union[str, None] = Field(default=None)
    scale: Union[str, None] = Field(default=None)
    thing_type: Union[str, None] = Field(default=None)
    is_stackable: Union[str, None] = Field(default=None)
    mask_image: Union[str, None] = Field(default=None)
    update_item_index: int = Field()

class IncidentChanceDto(WireModel):
    value: float = Field()

class IncidentChanceRequestDto(WireModel):
    incident_def_name: Union[str, None] = Field(default=None)

class IncidentDefDto(WireModel):
    def_name: Union[str, None] = Field(default=None)
    label: Union[str, None] = Field(default=None)
    description: Union[str, None] = Field(default=None)
    base_chance: float = Field()
    base_chance_with_royalty: float = Field()
    letter_def_name: Union[str, None] = Field(default=None)
    population_effect: Union[str, None] = Field(default=None)
    letter_text: Union[str, None] = Field(default=None)
    min_population: int = Field()
    min_threat_points: float = Field()
    max_threat_points: float = Field()
    category: Union[str, None] = Field(default=None)
    should_ignore_recent_weighting: bool = Field()
    tags: Union[list[str], None] = Field(default=None)
    disallowed_biomes: Union[list[str], None] = Field(default=None)
    allowed_biomes: Union[list[str], None] = Field(default=None)
    require_colonists_present: bool = Field()

class IncidentDto(WireModel):
    incident_def: Union[str, None] = Field(default=None)
    label: Union[str, None] = Field(default=None)
    category: Union[str, None] = Field(default=None)
    incident_hour: float = Field()
    days_since_occurred: float = Field()

class IncidentParmsDto(WireModel):
    target: Union[str, None] = Field(default=None)
    points: float = Field()
    faction: Union[str, None] = Field(default=None)
    forced: bool = Field()
    custom_letter_label: Union[str, None] = Field(default=None)
    custom_letter_text: Union[str, None] = Field(default=None)
    custom_letter_def: Union[str, None] = Field(default=None)
    send_letter: bool = Field()
    letter_hyperlink_thing_defs: Union[list[str], None] = Field(default=None)
    letter_hyperlink_hediff_defs: Union[list[str], None] = Field(default=None)
    in_signal_end: Union[str, None] = Field(default=None)
    silent: bool = Field()
    spawn_center: Union[str, None] = Field(default=None)
    spawn_rotation: Union[str, None] = Field(default=None)
    generate_fighters_only: bool = Field()
    dont_use_single_use_rocket_launchers: bool = Field()
    raid_strategy: Union[str, None] = Field(default=None)
    raid_arrival_mode: Union[str, None] = Field(default=None)
    raid_force_one_downed: bool = Field()
    raid_never_flee_individual: bool = Field()
    raid_arrival_mode_for_quick_military_aid: bool = Field()
    raid_age_restriction: Union[str, None] = Field(default=None)
    biocode_weapons_chance: float = Field()
    biocode_apparel_chance: float = Field()
    pawn_groups: Union[dict[str, int], None] = Field(default=None)
    pawn_group_maker_seed: Union[Union[int, None], None] = Field(default=None)
    pawn_ideo: Union[str, None] = Field(default=None)
    lord: Union[str, None] = Field(default=None)
    pawn_kind: Union[str, None] = Field(default=None)
    pawn_count: int = Field()
    pawn_group_kind: Union[str, None] = Field(default=None)
    trader_kind: Union[str, None] = Field(default=None)
    quest: Union[str, None] = Field(default=None)
    quest_script_def: Union[str, None] = Field(default=None)
    quest_tag: Union[str, None] = Field(default=None)
    mech_cluster_sketch: Union[str, None] = Field(default=None)
    can_timeout_or_flee: bool = Field()
    can_steal: bool = Field()
    can_kidnap: bool = Field()
    controller_pawn: Union[str, None] = Field(default=None)
    infestation_loc_override: Union[str, None] = Field(default=None)
    attack_targets: Union[list[str], None] = Field(default=None)
    gifts: Union[list[str], None] = Field(default=None)
    total_body_size: float = Field()
    psychic_ritual_def: Union[str, None] = Field(default=None)
    point_multiplier: float = Field()
    bypass_storyteller_settings: bool = Field()
    store_generated_neutral_pawns: Union[list[str], None] = Field(default=None)
    pawn_group_count: int = Field()
    pod_open_delay: int = Field()

class IncidentWeightDto(WireModel):
    def_name: Union[str, None] = Field(default=None)
    label: Union[str, None] = Field(default=None)
    category: Union[str, None] = Field(default=None)
    current_weight: float = Field()

class IncidentsDto(WireModel):
    incidents: Union[list[IncidentDto], None] = Field(default=None)

class Input_AddRelationRequestDto(WireModel):
    model_config = ConfigDict(strict=True, extra='allow', protected_namespaces=())
    pawn1_id: Union[int, None] = Field(default=None)
    pawn2_id: Union[int, None] = Field(default=None)
    relation_def_name: Union[str, None] = Field(default=None)

class Input_AllDefsRequestDto(WireModel):
    model_config = ConfigDict(strict=True, extra='allow', protected_namespaces=())
    filters: Union[list[str], None] = Field(default=None)

class Input_BillReorderRequest(WireModel):
    model_config = ConfigDict(strict=True, extra='allow', protected_namespaces=())
    offset: Union[int, None] = Field(default=None)

class Input_BillSuspendRequest(WireModel):
    model_config = ConfigDict(strict=True, extra='allow', protected_namespaces=())
    suspended: Union[bool, None] = Field(default=None)

class Input_BlueprintDto(WireModel):
    model_config = ConfigDict(strict=True, extra='allow', protected_namespaces=())
    width: Union[int, None] = Field(default=None)
    height: Union[int, None] = Field(default=None)
    floors: Union[list[Input_SavedTerrainDto], None] = Field(default=None)
    buildings: Union[list[Input_SavedBuildingDto], None] = Field(default=None)

class Input_Camera_CameraScreenshotRequestDto(WireModel):
    model_config = ConfigDict(strict=True, extra='allow', protected_namespaces=())
    format: Union[str, None] = Field(default=None)
    quality: Union[int, None] = Field(default=None)
    width: Union[Union[int, None], None] = Field(default=None)
    height: Union[Union[int, None], None] = Field(default=None)
    hide_ui: Union[bool, None] = Field(default=None)

class Input_Camera_NativeScreenshotRequestDto(WireModel):
    model_config = ConfigDict(strict=True, extra='allow', protected_namespaces=())
    file_name: Union[str, None] = Field(default=None)
    center_x: Union[Union[float, None], None] = Field(default=None)
    center_z: Union[Union[float, None], None] = Field(default=None)
    zoom_level: Union[Union[float, None], None] = Field(default=None)
    hide_u_i: Union[bool, None] = Field(default=None)

class Input_CheckZoneRequestDto(WireModel):
    model_config = ConfigDict(strict=True, extra='allow', protected_namespaces=())
    map_id: Union[int, None] = Field(default=None)
    point_a: Union[Input_PositionDto, None] = Field(default=None)
    point_b: Union[Input_PositionDto, None] = Field(default=None)

class Input_ColonistsWorkPrioritiesRequestDto(WireModel):
    model_config = ConfigDict(strict=True, extra='allow', protected_namespaces=())
    priorities: Union[list[Input_WorkPriorityRequestDto], None] = Field(default=None)

class Input_ConfigureModsRequestDto(WireModel):
    model_config = ConfigDict(strict=True, extra='allow', protected_namespaces=())
    package_ids: Union[list[str], None] = Field(default=None)
    restart_game: Union[bool, None] = Field(default=None)

class Input_CopyAreaRequestDto(WireModel):
    model_config = ConfigDict(strict=True, extra='allow', protected_namespaces=())
    map_id: Union[int, None] = Field(default=None)
    point_a: Union[Input_PositionDto, None] = Field(default=None)
    point_b: Union[Input_PositionDto, None] = Field(default=None)

class Input_CreateBillRequest(WireModel):
    model_config = ConfigDict(strict=True, extra='allow', protected_namespaces=())
    recipe_def_name: Union[str, None] = Field(default=None)
    repeat_mode: Union[str, None] = Field(default=None)
    repeat_count: Union[Union[int, None], None] = Field(default=None)
    target_count: Union[Union[int, None], None] = Field(default=None)
    store_mode: Union[str, None] = Field(default=None)
    suspended: Union[Union[bool, None], None] = Field(default=None)
    pause_when_satisfied: Union[Union[bool, None], None] = Field(default=None)
    unpause_when_you_have: Union[Union[int, None], None] = Field(default=None)
    include_equipped: Union[Union[bool, None], None] = Field(default=None)
    include_tainted: Union[Union[bool, None], None] = Field(default=None)
    hp_range: Union[Input_FloatRange, None] = Field(default=None)
    quality_range: Union[Input_IntRangeDto, None] = Field(default=None)
    limit_to_allowed_stuff: Union[Union[bool, None], None] = Field(default=None)
    ingredient_search_radius: Union[Union[float, None], None] = Field(default=None)
    allowed_skill_range: Union[Input_IntRangeDto, None] = Field(default=None)
    pawn_restriction_id: Union[Union[int, None], None] = Field(default=None)
    player_custom_name: Union[str, None] = Field(default=None)
    allowed_materials: Union[list[str], None] = Field(default=None)

class Input_CreateGrowingZoneRequestDto(WireModel):
    model_config = ConfigDict(strict=True, extra='allow', protected_namespaces=())
    map_id: Union[int, None] = Field(default=None)
    plant_def: Union[str, None] = Field(default=None)
    point_a: Union[Input_PositionDto, None] = Field(default=None)
    point_b: Union[Input_PositionDto, None] = Field(default=None)

class Input_CreateStockpileRequestDto(WireModel):
    model_config = ConfigDict(strict=True, extra='allow', protected_namespaces=())
    map_id: Union[int, None] = Field(default=None)
    point_a: Union[Input_PositionDto, None] = Field(default=None)
    point_b: Union[Input_PositionDto, None] = Field(default=None)
    name: Union[str, None] = Field(default=None)
    priority: Union[Union[int, None], None] = Field(default=None)
    allowed_item_defs: Union[list[str], None] = Field(default=None)
    allowed_item_categories: Union[list[str], None] = Field(default=None)
    min_hit_points_percent: Union[Union[float, None], None] = Field(default=None)
    max_hit_points_percent: Union[Union[float, None], None] = Field(default=None)
    min_quality: Union[str, None] = Field(default=None)
    max_quality: Union[str, None] = Field(default=None)

class Input_DebugConsoleRequest(WireModel):
    model_config = ConfigDict(strict=True, extra='allow', protected_namespaces=())
    action: Union[str, None] = Field(default=None)
    message: Union[str, None] = Field(default=None)

class Input_DesignateRequestDto(WireModel):
    model_config = ConfigDict(strict=True, extra='allow', protected_namespaces=())
    map_id: Union[int, None] = Field(default=None)
    type: Union[str, None] = Field(default=None)
    point_a: Union[Input_PositionDto, None] = Field(default=None)
    point_b: Union[Input_PositionDto, None] = Field(default=None)

class Input_DestroyRectRequestDto(WireModel):
    model_config = ConfigDict(strict=True, extra='allow', protected_namespaces=())
    map_id: Union[int, None] = Field(default=None)
    point_a: Union[Input_PositionDto, None] = Field(default=None)
    point_b: Union[Input_PositionDto, None] = Field(default=None)

class Input_DialogOptionDto(WireModel):
    model_config = ConfigDict(strict=True, extra='allow', protected_namespaces=())
    label: Union[str, None] = Field(default=None)
    action_id: Union[str, None] = Field(default=None)
    resolve_tree: Union[bool, None] = Field(default=None)

class Input_FactionChangeRelationRequestDto(WireModel):
    model_config = ConfigDict(strict=True, extra='allow', protected_namespaces=())
    id: Union[int, None] = Field(default=None)
    other_id: Union[int, None] = Field(default=None)
    value: Union[int, None] = Field(default=None)
    send_message: Union[bool, None] = Field(default=None)
    can_send_hostility_letter: Union[bool, None] = Field(default=None)

class Input_FloatRange(WireModel):
    model_config = ConfigDict(strict=True, extra='allow', protected_namespaces=())
    min: Union[float, None] = Field(default=None)
    max: Union[float, None] = Field(default=None)

class Input_ForceInteractionRequestDto(WireModel):
    model_config = ConfigDict(strict=True, extra='allow', protected_namespaces=())
    initiator_id: Union[int, None] = Field(default=None)
    recipient_id: Union[int, None] = Field(default=None)
    interaction_def_name: Union[str, None] = Field(default=None)

class Input_GameLoadRequestDto(WireModel):
    model_config = ConfigDict(strict=True, extra='allow', protected_namespaces=())
    file_name: Union[str, None] = Field(default=None)
    check_version: Union[bool, None] = Field(default=None)
    skip_mod_mismatch: Union[bool, None] = Field(default=None)

class Input_GameSaveRequestDto(WireModel):
    model_config = ConfigDict(strict=True, extra='allow', protected_namespaces=())
    file_name: Union[str, None] = Field(default=None)

class Input_ImageUploadRequest(WireModel):
    model_config = ConfigDict(strict=True, extra='allow', protected_namespaces=())
    name: Union[str, None] = Field(default=None)
    image: Union[str, None] = Field(default=None)
    direction: Union[str, None] = Field(default=None)
    offset: Union[str, None] = Field(default=None)
    scale: Union[str, None] = Field(default=None)
    thing_type: Union[str, None] = Field(default=None)
    is_stackable: Union[str, None] = Field(default=None)
    mask_image: Union[str, None] = Field(default=None)
    update_item_index: Union[int, None] = Field(default=None)

class Input_IncidentChanceRequestDto(WireModel):
    model_config = ConfigDict(strict=True, extra='allow', protected_namespaces=())
    incident_def_name: Union[str, None] = Field(default=None)

class Input_IncidentParmsDto(WireModel):
    model_config = ConfigDict(strict=True, extra='allow', protected_namespaces=())
    target: Union[str, None] = Field(default=None)
    points: Union[float, None] = Field(default=None)
    faction: Union[str, None] = Field(default=None)
    forced: Union[bool, None] = Field(default=None)
    custom_letter_label: Union[str, None] = Field(default=None)
    custom_letter_text: Union[str, None] = Field(default=None)
    custom_letter_def: Union[str, None] = Field(default=None)
    send_letter: Union[bool, None] = Field(default=None)
    letter_hyperlink_thing_defs: Union[list[str], None] = Field(default=None)
    letter_hyperlink_hediff_defs: Union[list[str], None] = Field(default=None)
    in_signal_end: Union[str, None] = Field(default=None)
    silent: Union[bool, None] = Field(default=None)
    spawn_center: Union[str, None] = Field(default=None)
    spawn_rotation: Union[str, None] = Field(default=None)
    generate_fighters_only: Union[bool, None] = Field(default=None)
    dont_use_single_use_rocket_launchers: Union[bool, None] = Field(default=None)
    raid_strategy: Union[str, None] = Field(default=None)
    raid_arrival_mode: Union[str, None] = Field(default=None)
    raid_force_one_downed: Union[bool, None] = Field(default=None)
    raid_never_flee_individual: Union[bool, None] = Field(default=None)
    raid_arrival_mode_for_quick_military_aid: Union[bool, None] = Field(default=None)
    raid_age_restriction: Union[str, None] = Field(default=None)
    biocode_weapons_chance: Union[float, None] = Field(default=None)
    biocode_apparel_chance: Union[float, None] = Field(default=None)
    pawn_groups: Union[dict[str, int], None] = Field(default=None)
    pawn_group_maker_seed: Union[Union[int, None], None] = Field(default=None)
    pawn_ideo: Union[str, None] = Field(default=None)
    lord: Union[str, None] = Field(default=None)
    pawn_kind: Union[str, None] = Field(default=None)
    pawn_count: Union[int, None] = Field(default=None)
    pawn_group_kind: Union[str, None] = Field(default=None)
    trader_kind: Union[str, None] = Field(default=None)
    quest: Union[str, None] = Field(default=None)
    quest_script_def: Union[str, None] = Field(default=None)
    quest_tag: Union[str, None] = Field(default=None)
    mech_cluster_sketch: Union[str, None] = Field(default=None)
    can_timeout_or_flee: Union[bool, None] = Field(default=None)
    can_steal: Union[bool, None] = Field(default=None)
    can_kidnap: Union[bool, None] = Field(default=None)
    controller_pawn: Union[str, None] = Field(default=None)
    infestation_loc_override: Union[str, None] = Field(default=None)
    attack_targets: Union[list[str], None] = Field(default=None)
    gifts: Union[list[str], None] = Field(default=None)
    total_body_size: Union[float, None] = Field(default=None)
    psychic_ritual_def: Union[str, None] = Field(default=None)
    point_multiplier: Union[float, None] = Field(default=None)
    bypass_storyteller_settings: Union[bool, None] = Field(default=None)
    store_generated_neutral_pawns: Union[list[str], None] = Field(default=None)
    pawn_group_count: Union[int, None] = Field(default=None)
    pod_open_delay: Union[int, None] = Field(default=None)

class Input_IntRangeDto(WireModel):
    model_config = ConfigDict(strict=True, extra='allow', protected_namespaces=())
    min: Union[int, None] = Field(default=None)
    max: Union[int, None] = Field(default=None)

class Input_ItemDto(WireModel):
    model_config = ConfigDict(strict=True, extra='allow', protected_namespaces=())
    def_name: Union[str, None] = Field(default=None)
    count: Union[int, None] = Field(default=None)

class Input_LearningConceptMarkDto(WireModel):
    model_config = ConfigDict(strict=True, extra='allow', protected_namespaces=())
    def_name: Union[str, None] = Field(default=None)
    is_completed: Union[bool, None] = Field(default=None)

class Input_LordCreateRequestDto(WireModel):
    model_config = ConfigDict(strict=True, extra='allow', protected_namespaces=())
    faction: Union[str, None] = Field(default=None)
    map_id: Union[str, None] = Field(default=None)
    pawn_ids: Union[list[int], None] = Field(default=None)
    job_type: Union[str, None] = Field(default=None)
    position: Union[Input_PositionDto, None] = Field(default=None)
    target_ids: Union[list[int], None] = Field(default=None)

class Input_MedicalBedRestRequestDto(WireModel):
    model_config = ConfigDict(strict=True, extra='allow', protected_namespaces=())
    patient_pawn_id: Union[int, None] = Field(default=None)
    bed_building_id: Union[Union[int, None], None] = Field(default=None)

class Input_MedicalTendRequestDto(WireModel):
    model_config = ConfigDict(strict=True, extra='allow', protected_namespaces=())
    patient_pawn_id: Union[int, None] = Field(default=None)
    doctor_pawn_id: Union[Union[int, None], None] = Field(default=None)

class Input_NewGameStartRequestDto(WireModel):
    model_config = ConfigDict(strict=True, extra='allow', protected_namespaces=())
    storyteller_name: Union[str, None] = Field(default=None)
    difficulty_name: Union[str, None] = Field(default=None)
    map_size: Union[int, None] = Field(default=None)
    permadeath: Union[bool, None] = Field(default=None)
    planet_coverage: Union[float, None] = Field(default=None)
    world_seed: Union[str, None] = Field(default=None)
    starting_tile: Union[str, None] = Field(default=None)
    starting_season: Union[str, None] = Field(default=None)
    overall_rainfall: Union[int, None] = Field(default=None)
    overall_temperature: Union[int, None] = Field(default=None)
    overall_population: Union[int, None] = Field(default=None)
    landmark_density: Union[int, None] = Field(default=None)

class Input_OverlayRequestDto(WireModel):
    model_config = ConfigDict(strict=True, extra='allow', protected_namespaces=())
    text: Union[str, None] = Field(default=None)
    duration: Union[float, None] = Field(default=None)
    color: Union[str, None] = Field(default=None)
    scale: Union[float, None] = Field(default=None)

class Input_PasteAreaRequestDto(WireModel):
    model_config = ConfigDict(strict=True, extra='allow', protected_namespaces=())
    map_id: Union[int, None] = Field(default=None)
    position: Union[Input_PositionDto, None] = Field(default=None)
    blueprint: Union[Input_BlueprintDto, None] = Field(default=None)
    clear_obstacles: Union[bool, None] = Field(default=None)

class Input_PawnApparelRequest(WireModel):
    model_config = ConfigDict(strict=True, extra='allow', protected_namespaces=())
    pawn_id: Union[int, None] = Field(default=None)
    drop_apparel: Union[bool, None] = Field(default=None)
    drop_weapons: Union[bool, None] = Field(default=None)

class Input_PawnBasicRequest(WireModel):
    model_config = ConfigDict(strict=True, extra='allow', protected_namespaces=())
    pawn_id: Union[int, None] = Field(default=None)
    name: Union[str, None] = Field(default=None)
    first_name: Union[str, None] = Field(default=None)
    last_name: Union[str, None] = Field(default=None)
    nick_name: Union[str, None] = Field(default=None)
    gender: Union[str, None] = Field(default=None)
    biological_age: Union[Union[int, None], None] = Field(default=None)
    chronological_age: Union[Union[int, None], None] = Field(default=None)

class Input_PawnFactionRequest(WireModel):
    model_config = ConfigDict(strict=True, extra='allow', protected_namespaces=())
    pawn_id: Union[int, None] = Field(default=None)
    set_faction: Union[str, None] = Field(default=None)
    make_colonist: Union[bool, None] = Field(default=None)
    make_prisoner: Union[bool, None] = Field(default=None)
    release_prisoner: Union[bool, None] = Field(default=None)

class Input_PawnHealthRequest(WireModel):
    model_config = ConfigDict(strict=True, extra='allow', protected_namespaces=())
    pawn_id: Union[int, None] = Field(default=None)
    heal_all_injuries: Union[bool, None] = Field(default=None)
    restore_body_parts: Union[bool, None] = Field(default=None)
    remove_all_diseases: Union[bool, None] = Field(default=None)

class Input_PawnInventoryRequest(WireModel):
    model_config = ConfigDict(strict=True, extra='allow', protected_namespaces=())
    pawn_id: Union[int, None] = Field(default=None)
    drop_inventory: Union[bool, None] = Field(default=None)
    clear_inventory: Union[bool, None] = Field(default=None)
    add_items: Union[list[Input_ItemDto], None] = Field(default=None)

class Input_PawnJobRequestDto(WireModel):
    model_config = ConfigDict(strict=True, extra='allow', protected_namespaces=())
    pawn_id: Union[int, None] = Field(default=None)
    job_def: Union[str, None] = Field(default=None)
    target_thing_id: Union[Union[int, None], None] = Field(default=None)
    target_position: Union[Input_PositionDto, None] = Field(default=None)

class Input_PawnNeedsRequest(WireModel):
    model_config = ConfigDict(strict=True, extra='allow', protected_namespaces=())
    pawn_id: Union[int, None] = Field(default=None)
    food: Union[Union[float, None], None] = Field(default=None)
    rest: Union[Union[float, None], None] = Field(default=None)
    mood: Union[Union[float, None], None] = Field(default=None)

class Input_PawnPositionRequest(WireModel):
    model_config = ConfigDict(strict=True, extra='allow', protected_namespaces=())
    pawn_id: Union[int, None] = Field(default=None)
    map_id: Union[str, None] = Field(default=None)
    position: Union[Input_PositionDto, None] = Field(default=None)

class Input_PawnSkillsRequest(WireModel):
    model_config = ConfigDict(strict=True, extra='allow', protected_namespaces=())
    pawn_id: Union[int, None] = Field(default=None)
    skills: Union[list[Input_SkillEntryDto], None] = Field(default=None)

class Input_PawnSpawnRequestDto(WireModel):
    model_config = ConfigDict(strict=True, extra='allow', protected_namespaces=())
    pawn_kind: Union[str, None] = Field(default=None)
    faction: Union[str, None] = Field(default=None)
    xenotype: Union[str, None] = Field(default=None)
    gender: Union[str, None] = Field(default=None)
    biological_age: Union[Union[float, None], None] = Field(default=None)
    chronological_age: Union[Union[float, None], None] = Field(default=None)
    allow_dead: Union[bool, None] = Field(default=None)
    allow_downed: Union[bool, None] = Field(default=None)
    can_generate_pawn_relations: Union[bool, None] = Field(default=None)
    must_be_capable_of_violence: Union[bool, None] = Field(default=None)
    allow_gay: Union[bool, None] = Field(default=None)
    allow_pregnant: Union[bool, None] = Field(default=None)
    allow_food: Union[bool, None] = Field(default=None)
    allow_addictions: Union[bool, None] = Field(default=None)
    inhabitant: Union[bool, None] = Field(default=None)
    first_name: Union[str, None] = Field(default=None)
    last_name: Union[str, None] = Field(default=None)
    nick_name: Union[str, None] = Field(default=None)
    map_id: Union[str, None] = Field(default=None)
    position: Union[Input_PositionDto, None] = Field(default=None)

class Input_PawnStatusRequest(WireModel):
    model_config = ConfigDict(strict=True, extra='allow', protected_namespaces=())
    pawn_id: Union[int, None] = Field(default=None)
    is_drafted: Union[Union[bool, None], None] = Field(default=None)
    kill: Union[bool, None] = Field(default=None)
    resurrect: Union[bool, None] = Field(default=None)

class Input_PawnTimeAssignmentRequestDto(WireModel):
    model_config = ConfigDict(strict=True, extra='allow', protected_namespaces=())
    pawn_id: Union[int, None] = Field(default=None)
    hour: Union[int, None] = Field(default=None)
    assignment: Union[str, None] = Field(default=None)

class Input_PawnTraitsRequest(WireModel):
    model_config = ConfigDict(strict=True, extra='allow', protected_namespaces=())
    pawn_id: Union[int, None] = Field(default=None)
    add_traits: Union[list[Input_TraitEntryDto], None] = Field(default=None)
    remove_traits: Union[list[str], None] = Field(default=None)

class Input_PositionDto(WireModel):
    model_config = ConfigDict(strict=True, extra='allow', protected_namespaces=())
    x: Union[int, None] = Field(default=None)
    y: Union[int, None] = Field(default=None)
    z: Union[int, None] = Field(default=None)

class Input_RepairPositionsRequestDto(WireModel):
    model_config = ConfigDict(strict=True, extra='allow', protected_namespaces=())
    map_id: Union[int, None] = Field(default=None)
    positions: Union[list[Input_PositionDto], None] = Field(default=None)

class Input_RepairRectRequestDto(WireModel):
    model_config = ConfigDict(strict=True, extra='allow', protected_namespaces=())
    map_id: Union[int, None] = Field(default=None)
    point_a: Union[Input_PositionDto, None] = Field(default=None)
    point_b: Union[Input_PositionDto, None] = Field(default=None)

class Input_SavedBuildingDto(WireModel):
    model_config = ConfigDict(strict=True, extra='allow', protected_namespaces=())
    def_name: Union[str, None] = Field(default=None)
    stuff_def_name: Union[str, None] = Field(default=None)
    rel_x: Union[int, None] = Field(default=None)
    rel_z: Union[int, None] = Field(default=None)
    rotation: Union[int, None] = Field(default=None)

class Input_SavedTerrainDto(WireModel):
    model_config = ConfigDict(strict=True, extra='allow', protected_namespaces=())
    def_name: Union[str, None] = Field(default=None)
    rel_x: Union[int, None] = Field(default=None)
    rel_z: Union[int, None] = Field(default=None)

class Input_SelectAreaRequestDto(WireModel):
    model_config = ConfigDict(strict=True, extra='allow', protected_namespaces=())
    position_a: Union[Input_PositionDto, None] = Field(default=None)
    position_b: Union[Input_PositionDto, None] = Field(default=None)

class Input_SendLetterRequestDto(WireModel):
    model_config = ConfigDict(strict=True, extra='allow', protected_namespaces=())
    label: Union[str, None] = Field(default=None)
    message: Union[str, None] = Field(default=None)
    letter_def: Union[str, None] = Field(default=None)
    map_id: Union[str, None] = Field(default=None)
    look_target_thing_id: Union[str, None] = Field(default=None)
    faction_order_id: Union[str, None] = Field(default=None)
    hyperlink_thing_defs: Union[list[str], None] = Field(default=None)
    quest_id: Union[str, None] = Field(default=None)
    debug_info: Union[str, None] = Field(default=None)
    delay_ticks: Union[int, None] = Field(default=None)
    play_sound: Union[bool, None] = Field(default=None)

class Input_SetForbiddenRequestDto(WireModel):
    model_config = ConfigDict(strict=True, extra='allow', protected_namespaces=())
    thing_ids: Union[list[int], None] = Field(default=None)
    map_id: Union[int, None] = Field(default=None)
    forbidden: Union[bool, None] = Field(default=None)

class Input_SkillEntryDto(WireModel):
    model_config = ConfigDict(strict=True, extra='allow', protected_namespaces=())
    skill_name: Union[str, None] = Field(default=None)
    level: Union[Union[int, None], None] = Field(default=None)
    passion: Union[Union[int, None], None] = Field(default=None)

class Input_SpawnDropPodRequestDto(WireModel):
    model_config = ConfigDict(strict=True, extra='allow', protected_namespaces=())
    map_id: Union[int, None] = Field(default=None)
    position: Union[Input_PositionDto, None] = Field(default=None)
    items: Union[list[Input_ThingDto], None] = Field(default=None)
    faction: Union[str, None] = Field(default=None)
    open_delay: Union[bool, None] = Field(default=None)

class Input_SpawnItemRequestDto(WireModel):
    model_config = ConfigDict(strict=True, extra='allow', protected_namespaces=())
    def_name: Union[str, None] = Field(default=None)
    stuff_def_name: Union[str, None] = Field(default=None)
    quality: Union[str, None] = Field(default=None)
    amount: Union[int, None] = Field(default=None)
    x: Union[int, None] = Field(default=None)
    z: Union[int, None] = Field(default=None)

class Input_StreamConfigDto(WireModel):
    model_config = ConfigDict(strict=True, extra='allow', protected_namespaces=())
    port: Union[int, None] = Field(default=None)
    address: Union[str, None] = Field(default=None)
    frame_width: Union[int, None] = Field(default=None)
    frame_height: Union[int, None] = Field(default=None)
    target_fps: Union[int, None] = Field(default=None)
    jpeg_quality: Union[int, None] = Field(default=None)

class Input_StuffColorRequest(WireModel):
    model_config = ConfigDict(strict=True, extra='allow', protected_namespaces=())
    name: Union[str, None] = Field(default=None)
    hex: Union[str, None] = Field(default=None)

class Input_ThingDto(WireModel):
    model_config = ConfigDict(strict=True, extra='allow', protected_namespaces=())
    thing_id: Union[int, None] = Field(default=None)
    def_name: Union[str, None] = Field(default=None)
    label: Union[str, None] = Field(default=None)
    categories: Union[list[str], None] = Field(default=None)
    position: Union[Input_PositionDto, None] = Field(default=None)
    rotation: Union[int, None] = Field(default=None)
    size: Union[Input_PositionDto, None] = Field(default=None)
    stack_count: Union[int, None] = Field(default=None)
    market_value: Union[float, None] = Field(default=None)
    is_forbidden: Union[bool, None] = Field(default=None)
    quality: Union[int, None] = Field(default=None)
    stuff_def_name: Union[str, None] = Field(default=None)
    hit_points: Union[int, None] = Field(default=None)
    max_hit_points: Union[int, None] = Field(default=None)
    description: Union[str, None] = Field(default=None)

class Input_ThingsAtCellRequestDto(WireModel):
    model_config = ConfigDict(strict=True, extra='allow', protected_namespaces=())
    map_id: Union[int, None] = Field(default=None)
    position: Union[Input_PositionDto, None] = Field(default=None)

class Input_TraitEntryDto(WireModel):
    model_config = ConfigDict(strict=True, extra='allow', protected_namespaces=())
    trait_name: Union[str, None] = Field(default=None)
    degree: Union[Union[int, None], None] = Field(default=None)

class Input_TriggerIncidentRequestDto(WireModel):
    model_config = ConfigDict(strict=True, extra='allow', protected_namespaces=())
    name: Union[str, None] = Field(default=None)
    map_id: Union[str, None] = Field(default=None)
    incident_parms: Union[Input_IncidentParmsDto, None] = Field(default=None)

class Input_UpdateBillRequest(WireModel):
    model_config = ConfigDict(strict=True, extra='allow', protected_namespaces=())
    repeat_mode: Union[str, None] = Field(default=None)
    repeat_count: Union[Union[int, None], None] = Field(default=None)
    target_count: Union[Union[int, None], None] = Field(default=None)
    store_mode: Union[str, None] = Field(default=None)
    suspended: Union[Union[bool, None], None] = Field(default=None)
    pause_when_satisfied: Union[Union[bool, None], None] = Field(default=None)
    unpause_when_you_have: Union[Union[int, None], None] = Field(default=None)
    include_equipped: Union[Union[bool, None], None] = Field(default=None)
    include_tainted: Union[Union[bool, None], None] = Field(default=None)
    hp_range: Union[Input_FloatRange, None] = Field(default=None)
    quality_range: Union[Input_IntRangeDto, None] = Field(default=None)
    limit_to_allowed_stuff: Union[Union[bool, None], None] = Field(default=None)
    ingredient_search_radius: Union[Union[float, None], None] = Field(default=None)
    allowed_skill_range: Union[Input_IntRangeDto, None] = Field(default=None)
    pawn_restriction_id: Union[Union[int, None], None] = Field(default=None)
    player_custom_name: Union[str, None] = Field(default=None)
    allowed_materials: Union[list[str], None] = Field(default=None)

class Input_UpdateStockpileRequestDto(WireModel):
    model_config = ConfigDict(strict=True, extra='allow', protected_namespaces=())
    zone_id: Union[int, None] = Field(default=None)
    name: Union[str, None] = Field(default=None)
    priority: Union[Union[int, None], None] = Field(default=None)
    add_item_defs: Union[list[str], None] = Field(default=None)
    remove_item_defs: Union[list[str], None] = Field(default=None)
    add_item_categories: Union[list[str], None] = Field(default=None)
    remove_item_categories: Union[list[str], None] = Field(default=None)
    min_hit_points_percent: Union[Union[float, None], None] = Field(default=None)
    max_hit_points_percent: Union[Union[float, None], None] = Field(default=None)
    min_quality: Union[str, None] = Field(default=None)
    max_quality: Union[str, None] = Field(default=None)

class Input_WindowCloseRequestDto(WireModel):
    model_config = ConfigDict(strict=True, extra='allow', protected_namespaces=())
    window_types: Union[list[str], None] = Field(default=None)
    force_pause_only: Union[bool, None] = Field(default=None)

class Input_WindowDialogRequestDto(WireModel):
    model_config = ConfigDict(strict=True, extra='allow', protected_namespaces=())
    title: Union[str, None] = Field(default=None)
    text: Union[str, None] = Field(default=None)
    options: Union[list[Input_DialogOptionDto], None] = Field(default=None)

class Input_WindowMessageRequestDto(WireModel):
    model_config = ConfigDict(strict=True, extra='allow', protected_namespaces=())
    title: Union[str, None] = Field(default=None)
    text: Union[str, None] = Field(default=None)
    button_text: Union[str, None] = Field(default=None)

class Input_WorkPriorityRequestDto(WireModel):
    model_config = ConfigDict(strict=True, extra='allow', protected_namespaces=())
    id: Union[int, None] = Field(default=None)
    work: Union[str, None] = Field(default=None)
    priority: Union[int, None] = Field(default=None)

class IntRangeDto(WireModel):
    min: int = Field()
    max: int = Field()

class InteractionDefDto(WireModel):
    def_name: Union[str, None] = Field(default=None)
    label: Union[str, None] = Field(default=None)
    description: Union[str, None] = Field(default=None)

class InteractionLogEntryDto(WireModel):
    initiator_id: int = Field()
    initiator_name: Union[str, None] = Field(default=None)
    recipient_id: int = Field()
    recipient_name: Union[str, None] = Field(default=None)
    interaction_def_name: Union[str, None] = Field(default=None)
    interaction_label: Union[str, None] = Field(default=None)
    text: Union[str, None] = Field(default=None)
    ticks: int = Field()
    time_ago: Union[str, None] = Field(default=None)

class ItemDto(WireModel):
    def_name: Union[str, None] = Field(default=None)
    count: int = Field()

class ItemRecipesDto(WireModel):
    item_def_name: Union[str, None] = Field(default=None)
    item_label: Union[str, None] = Field(default=None)
    recipes: Union[list[ThingRecipeDto], None] = Field(default=None)

class JobDefDto(WireModel):
    def_name: Union[str, None] = Field(default=None)
    label: Union[str, None] = Field(default=None)
    description: Union[str, None] = Field(default=None)
    player_interruptible: bool = Field()
    always_show_weapon: bool = Field()
    never_show_weapon: bool = Field()
    suspendable: bool = Field()

class JoyKindDefDto(WireModel):
    def_name: Union[str, None] = Field(default=None)
    label: Union[str, None] = Field(default=None)

class LearningConceptDto(WireModel):
    def_name: Union[str, None] = Field(default=None)
    label: Union[str, None] = Field(default=None)
    help_text: Union[str, None] = Field(default=None)
    knowledge_progress: float = Field()
    is_completed: bool = Field()

class LearningConceptMarkDto(WireModel):
    def_name: Union[str, None] = Field(default=None)
    is_completed: bool = Field()

class LifeStageAgeDto(WireModel):
    life_stage: Union[str, None] = Field(default=None)
    min_age: float = Field()

class LordCreateDto(WireModel):
    lord_id: int = Field()
    member_count: int = Field()

class LordCreateRequestDto(WireModel):
    faction: Union[str, None] = Field(default=None)
    map_id: Union[str, None] = Field(default=None)
    pawn_ids: Union[list[int], None] = Field(default=None)
    job_type: Union[str, None] = Field(default=None)
    position: Union[PositionDto, None] = Field(default=None)
    target_ids: Union[list[int], None] = Field(default=None)

class LordDto(WireModel):
    load_id: int = Field()
    faction_name: Union[str, None] = Field(default=None)
    faction_def_name: Union[str, None] = Field(default=None)
    lord_job_type: Union[str, None] = Field(default=None)
    current_toil_name: Union[str, None] = Field(default=None)
    ticks_in_toil: int = Field()
    num_pawns_lost_violently: int = Field()
    num_pawns_ever_gained: int = Field()
    owned_pawn_ids: Union[list[str], None] = Field(default=None)
    owned_building_ids: Union[list[str], None] = Field(default=None)
    quest_tags: Union[list[str], None] = Field(default=None)
    in_signal_leave: Union[str, None] = Field(default=None)
    any_active_pawn: bool = Field()

class MapCreaturesSummaryDto(WireModel):
    colonists_count: int = Field()
    prisoners_count: int = Field()
    enemies_count: int = Field()
    animals_count: int = Field()
    insectoids_count: int = Field()
    mechanoids_count: int = Field()

class MapDto(WireModel):
    id: int = Field()
    index: int = Field()
    seed: int = Field()
    faction_id: Union[str, None] = Field(default=None)
    is_player_home: bool = Field()
    is_pocket_map: bool = Field()
    is_temp_incident_map: bool = Field()
    size: Union[str, None] = Field(default=None)

class MapFarmSummaryDto(WireModel):
    total_growing_zones: int = Field()
    total_plants: int = Field()
    total_expected_yield: int = Field()
    total_infected_plants: int = Field()
    growth_progress_average: float = Field()
    crop_types: Union[list[CropTypeDto], None] = Field(default=None)

class MapPowerInfoDto(WireModel):
    current_power: int = Field()
    total_possible_power: int = Field()
    currently_stored_power: int = Field()
    total_power_storage: int = Field()
    total_consumption: int = Field()
    consumption_power_on: int = Field()
    produce_power_buildings: Union[list[int], None] = Field(default=None)
    consume_power_buildings: Union[list[int], None] = Field(default=None)
    store_power_buildings: Union[list[int], None] = Field(default=None)

class MapRoomsDto(WireModel):
    rooms: Union[list[RoomDto], None] = Field(default=None)

class MapTerrainDto(WireModel):
    width: int = Field()
    height: int = Field()
    palette: Union[list[str], None] = Field(default=None)
    grid: Union[list[int], None] = Field(default=None)
    floor_palette: Union[list[str], None] = Field(default=None)
    floor_grid: Union[list[int], None] = Field(default=None)

class MapTimeDto(WireModel):
    datetime: Union[str, None] = Field(default=None)

class MapWeatherDto(WireModel):
    weather: Union[str, None] = Field(default=None)
    temperature: float = Field()

class MapZonesDto(WireModel):
    zones: Union[list[ZoneDto], None] = Field(default=None)
    areas: Union[list[ZoneDto], None] = Field(default=None)

class Map_OreDataDto(WireModel):
    map_width: int = Field()
    ores: Union[dict[str, Map_OreGroupDto], None] = Field(default=None)

class Map_OreGroupDto(WireModel):
    max_hp: int = Field()
    cells: Union[list[int], None] = Field(default=None)
    hp: Union[list[int], None] = Field(default=None)

class MaterialsAtlasList(WireModel):
    materials: Union[list[str], None] = Field(default=None)

class MedicalBedRestRequestDto(WireModel):
    patient_pawn_id: int = Field()
    bed_building_id: Union[Union[int, None], None] = Field(default=None)

class MedicalInfoDto(WireModel):
    is_dead: bool = Field()
    is_downed: bool = Field()
    consciousness: float = Field()
    moving: float = Field()
    health: float = Field()
    hediffs: Union[list[HediffDto], None] = Field(default=None)
    medical_policy_id: int = Field()
    is_self_tend_allowed: bool = Field()

class MedicalTendRequestDto(WireModel):
    patient_pawn_id: int = Field()
    doctor_pawn_id: Union[Union[int, None], None] = Field(default=None)

class MemeDefDto(WireModel):
    def_name: Union[str, None] = Field(default=None)
    label: Union[str, None] = Field(default=None)
    description: Union[str, None] = Field(default=None)

class MentalStateDefDto(WireModel):
    def_name: Union[str, None] = Field(default=None)
    label: Union[str, None] = Field(default=None)
    description: Union[str, None] = Field(default=None)
    recovery_mtb_days: Union[float, Literal['Infinity', '-Infinity', 'NaN']] = Field()
    is_aggro: bool = Field()

class ModInfoDto(WireModel):
    name: Union[str, None] = Field(default=None)
    package_id: Union[str, None] = Field(default=None)
    load_order: int = Field()
    author: Union[str, None] = Field(default=None)
    description: Union[str, None] = Field(default=None)
    url: Union[str, None] = Field(default=None)
    version: Union[str, None] = Field(default=None)
    supported_versions: Union[list[str], None] = Field(default=None)
    root_dir: Union[str, None] = Field(default=None)

class NeedDefDto(WireModel):
    def_name: Union[str, None] = Field(default=None)
    need_class_name: Union[str, None] = Field(default=None)
    freeze_in_mental_state: bool = Field()
    freeze_while_sleeping: bool = Field()
    seeker_fall_per_hour: float = Field()
    seeker_rise_per_hour: float = Field()
    fall_per_day: float = Field()
    show_unit_ticks: bool = Field()
    scale_bar: bool = Field()
    show_for_caravan_members: bool = Field()
    tutor_highlight_tag: Union[str, None] = Field(default=None)
    list_priority: int = Field()
    major: bool = Field()
    base_level: float = Field()
    show_on_need_list: bool = Field()
    developmental_stage_filter: Union[str, None] = Field(default=None)
    nullifying_precepts: Union[list[str], None] = Field(default=None)
    hediff_required_any: Union[list[str], None] = Field(default=None)
    title_required_any: Union[list[str], None] = Field(default=None)
    never_on_slave: bool = Field()
    never_on_prisoner: bool = Field()
    only_if_caused_by_trait: bool = Field()
    only_if_caused_by_gene: bool = Field()
    only_if_caused_by_hediff: bool = Field()
    slaves_only: bool = Field()
    colonists_only: bool = Field()
    player_mechs_only: bool = Field()
    colonist_and_prisoners_only: bool = Field()
    min_intelligence: Union[str, None] = Field(default=None)
    required_comps: Union[list[str], None] = Field(default=None)

class NewGameStartRequestDto(WireModel):
    storyteller_name: Union[str, None] = Field(default=None)
    difficulty_name: Union[str, None] = Field(default=None)
    map_size: int = Field()
    permadeath: bool = Field()
    planet_coverage: float = Field()
    world_seed: Union[str, None] = Field(default=None)
    starting_tile: Union[str, None] = Field(default=None)
    starting_season: Union[str, None] = Field(default=None)
    overall_rainfall: int = Field()
    overall_temperature: int = Field()
    overall_population: int = Field()
    landmark_density: int = Field()

class OpenWindowDto(WireModel):
    window_type: Union[str, None] = Field(default=None)
    force_pause: bool = Field()

class OpinionAboutPawnDto(WireModel):
    opinion: int = Field()
    opinion_about_me: int = Field()

class OpinionBreakdownDto(WireModel):
    thought_def_name: Union[str, None] = Field(default=None)
    label: Union[str, None] = Field(default=None)
    score: float = Field()

class OutfitDto(WireModel):
    id: int = Field()
    label: Union[str, None] = Field(default=None)
    filter: Union[ThingFilterDto, None] = Field(default=None)

class OverlayRequestDto(WireModel):
    text: Union[str, None] = Field(default=None)
    duration: float = Field()
    color: Union[str, None] = Field(default=None)
    scale: float = Field()

class PasteAreaRequestDto(WireModel):
    map_id: int = Field()
    position: Union[PositionDto, None] = Field(default=None)
    blueprint: Union[BlueprintDto, None] = Field(default=None)
    clear_obstacles: bool = Field()

class PawnApparelRequest(WireModel):
    pawn_id: int = Field()
    drop_apparel: bool = Field()
    drop_weapons: bool = Field()

class PawnBasicRequest(WireModel):
    pawn_id: int = Field()
    name: Union[str, None] = Field(default=None)
    first_name: Union[str, None] = Field(default=None)
    last_name: Union[str, None] = Field(default=None)
    nick_name: Union[str, None] = Field(default=None)
    gender: Union[str, None] = Field(default=None)
    biological_age: Union[Union[int, None], None] = Field(default=None)
    chronological_age: Union[Union[int, None], None] = Field(default=None)

class PawnDetailedDto(WireModel):
    body_size: float = Field()
    sleep: Union[Union[float, None], None] = Field(default=None)
    comfort: Union[Union[float, None], None] = Field(default=None)
    beauty: Union[Union[float, None], None] = Field(default=None)
    joy: Union[Union[float, None], None] = Field(default=None)
    energy: Union[Union[float, None], None] = Field(default=None)
    drugs_desire: Union[Union[float, None], None] = Field(default=None)
    surrounding_beauty: Union[Union[float, None], None] = Field(default=None)
    fresh_air: Union[Union[float, None], None] = Field(default=None)
    work_info: Union[WorkInfoDto, None] = Field(default=None)
    policies_info: Union[PoliciesInfoDto, None] = Field(default=None)
    medical_info: Union[MedicalInfoDto, None] = Field(default=None)
    social_info: Union[SocialInfoDto, None] = Field(default=None)

class PawnDetailedRequestDto(WireModel):
    pawn: Union[PawnDto, None] = Field(default=None)
    detailes: Union[PawnDetailedDto, None] = Field(default=None)

class PawnDto(WireModel):
    id: int = Field()
    name: Union[str, None] = Field(default=None)
    gender: Union[str, None] = Field(default=None)
    age: int = Field()
    health: float = Field()
    mood: float = Field()
    hunger: float = Field()
    position: Union[PositionDto, None] = Field(default=None)

class PawnFactionRequest(WireModel):
    pawn_id: int = Field()
    set_faction: Union[str, None] = Field(default=None)
    make_colonist: bool = Field()
    make_prisoner: bool = Field()
    release_prisoner: bool = Field()

class PawnHealthRequest(WireModel):
    pawn_id: int = Field()
    heal_all_injuries: bool = Field()
    restore_body_parts: bool = Field()
    remove_all_diseases: bool = Field()

class PawnInteractionLogDto(WireModel):
    pawn_id: int = Field()
    interactions: Union[list[InteractionLogEntryDto], None] = Field(default=None)
    count: int = Field()

class PawnInteractionStatusDto(WireModel):
    can_interact: bool = Field()
    cooldown_ticks: int = Field()
    cooldown_days: float = Field()
    last_interaction_def: Union[str, None] = Field(default=None)
    last_interaction_ticks: int = Field()

class PawnInventoryDto(WireModel):
    items: Union[list[ThingDto], None] = Field(default=None)
    apparels: Union[list[ThingDto], None] = Field(default=None)
    equipment: Union[list[ThingDto], None] = Field(default=None)

class PawnInventoryRequest(WireModel):
    pawn_id: int = Field()
    drop_inventory: bool = Field()
    clear_inventory: bool = Field()
    add_items: Union[list[ItemDto], None] = Field(default=None)

class PawnJobRequestDto(WireModel):
    pawn_id: int = Field()
    job_def: Union[str, None] = Field(default=None)
    target_thing_id: Union[Union[int, None], None] = Field(default=None)
    target_position: Union[PositionDto, None] = Field(default=None)

class PawnKindDefDto(WireModel):
    def_name: Union[str, None] = Field(default=None)
    label: Union[str, None] = Field(default=None)
    race: Union[str, None] = Field(default=None)
    combat_power: float = Field()
    weapon_tags: Union[list[str], None] = Field(default=None)
    apparel_tags: Union[list[str], None] = Field(default=None)
    base_health_scale: float = Field()
    base_body_size: float = Field()
    faction_tags: Union[list[str], None] = Field(default=None)

class PawnNeedsRequest(WireModel):
    pawn_id: int = Field()
    food: Union[Union[float, None], None] = Field(default=None)
    rest: Union[Union[float, None], None] = Field(default=None)
    mood: Union[Union[float, None], None] = Field(default=None)

class PawnOpinionDto(WireModel):
    target_pawn_id: int = Field()
    target_pawn_name: Union[str, None] = Field(default=None)
    opinion: int = Field()
    breakdown: Union[list[OpinionBreakdownDto], None] = Field(default=None)

class PawnPositionDto(WireModel):
    id: int = Field()
    x: int = Field()
    z: int = Field()
    map_id: int = Field()

class PawnPositionRequest(WireModel):
    pawn_id: int = Field()
    map_id: Union[str, None] = Field(default=None)
    position: Union[PositionDto, None] = Field(default=None)

class PawnRelationEntryDto(WireModel):
    other_pawn_id: int = Field()
    other_pawn_name: Union[str, None] = Field(default=None)
    relation_def_name: Union[str, None] = Field(default=None)
    relation_label: Union[str, None] = Field(default=None)

class PawnRelationsDto(WireModel):
    pawn_id: int = Field()
    relations: Union[list[PawnRelationEntryDto], None] = Field(default=None)

class PawnSkillsRequest(WireModel):
    pawn_id: int = Field()
    skills: Union[list[SkillEntryDto], None] = Field(default=None)

class PawnSpawnDto(WireModel):
    pawn_id: int = Field()
    name: Union[str, None] = Field(default=None)

class PawnSpawnRequestDto(WireModel):
    pawn_kind: Union[str, None] = Field(default=None)
    faction: Union[str, None] = Field(default=None)
    xenotype: Union[str, None] = Field(default=None)
    gender: Union[str, None] = Field(default=None)
    biological_age: Union[Union[float, None], None] = Field(default=None)
    chronological_age: Union[Union[float, None], None] = Field(default=None)
    allow_dead: bool = Field()
    allow_downed: bool = Field()
    can_generate_pawn_relations: bool = Field()
    must_be_capable_of_violence: bool = Field()
    allow_gay: bool = Field()
    allow_pregnant: bool = Field()
    allow_food: bool = Field()
    allow_addictions: bool = Field()
    inhabitant: bool = Field()
    first_name: Union[str, None] = Field(default=None)
    last_name: Union[str, None] = Field(default=None)
    nick_name: Union[str, None] = Field(default=None)
    map_id: Union[str, None] = Field(default=None)
    position: Union[PositionDto, None] = Field(default=None)

class PawnStatusRequest(WireModel):
    pawn_id: int = Field()
    is_drafted: Union[Union[bool, None], None] = Field(default=None)
    kill: bool = Field()
    resurrect: bool = Field()

class PawnTimeAssignmentRequestDto(WireModel):
    pawn_id: int = Field()
    hour: int = Field()
    assignment: Union[str, None] = Field(default=None)

class PawnTraitsRequest(WireModel):
    pawn_id: int = Field()
    add_traits: Union[list[TraitEntryDto], None] = Field(default=None)
    remove_traits: Union[list[str], None] = Field(default=None)

class PlantDefDto(WireModel):
    def_name: Union[str, None] = Field(default=None)
    label: Union[str, None] = Field(default=None)
    description: Union[str, None] = Field(default=None)
    fertility_min: float = Field()
    fertility_sensitivity: float = Field()
    grow_days: float = Field()
    harvested_thing_def: Union[str, None] = Field(default=None)

class PoliciesInfoDto(WireModel):
    food_policy_id: int = Field()
    hostility_response: int = Field()

class PositionDto(WireModel):
    x: int = Field()
    y: int = Field()
    z: int = Field()

class PreceptDefDto(WireModel):
    def_name: Union[str, None] = Field(default=None)
    label: Union[str, None] = Field(default=None)
    description: Union[str, None] = Field(default=None)
    impact: Union[str, None] = Field(default=None)

class QuestDto(WireModel):
    id: int = Field()
    quest_def: Union[str, None] = Field(default=None)
    name: Union[str, None] = Field(default=None)
    description: Union[str, None] = Field(default=None)
    state: Union[str, None] = Field(default=None)
    expiry_hours: float = Field()
    reward: Union[list[str], None] = Field(default=None)

class QuestsDto(WireModel):
    active_quests: Union[list[QuestDto], None] = Field(default=None)
    historical_quests: Union[list[QuestDto], None] = Field(default=None)

class RecipeDefDto(WireModel):
    def_name: Union[str, None] = Field(default=None)
    label: Union[str, None] = Field(default=None)
    description: Union[str, None] = Field(default=None)
    work_skill: Union[str, None] = Field(default=None)
    work_skill_learn_factor: float = Field()
    work_amount: float = Field()
    work_speed_stat: Union[str, None] = Field(default=None)
    efficiency_stat: Union[str, None] = Field(default=None)
    products: Union[list[str], None] = Field(default=None)
    skill_requirements: Union[list[SkillRequirementDto], None] = Field(default=None)

class RecipeDto(WireModel):
    def_name: Union[str, None] = Field(default=None)
    label: Union[str, None] = Field(default=None)
    description: Union[str, None] = Field(default=None)
    work_amount: float = Field()
    work_skill: Union[str, None] = Field(default=None)
    products: Union[list[RecipeProductDto], None] = Field(default=None)
    ingredients: Union[list[BillRecipeIngredientDto], None] = Field(default=None)

class RecipeIngredientDto(WireModel):
    summary: Union[str, None] = Field(default=None)
    count: float = Field()
    is_fixed_item: bool = Field()
    allowed_def_names: Union[list[str], None] = Field(default=None)

class RecipeProducerDto(WireModel):
    def_name: Union[str, None] = Field(default=None)
    label: Union[str, None] = Field(default=None)

class RecipeProductDto(WireModel):
    thing_def: Union[str, None] = Field(default=None)
    count: int = Field()

class RecipeSkillDto(WireModel):
    skill: Union[str, None] = Field(default=None)
    min_level: int = Field()

class RelationDto(WireModel):
    relation_def_name: Union[str, None] = Field(default=None)
    other_pawn_id: Union[str, None] = Field(default=None)
    other_pawn_name: Union[str, None] = Field(default=None)

class RepairPositionsRequestDto(WireModel):
    map_id: int = Field()
    positions: Union[list[PositionDto], None] = Field(default=None)

class RepairRectRequestDto(WireModel):
    map_id: int = Field()
    point_a: Union[PositionDto, None] = Field(default=None)
    point_b: Union[PositionDto, None] = Field(default=None)

class ResearchCategoryDto(WireModel):
    finished: int = Field()
    total: int = Field()
    percent_complete: float = Field()
    projects: Union[list[str], None] = Field(default=None)

class ResearchFinishedDto(WireModel):
    finished_projects: Union[list[str], None] = Field(default=None)

class ResearchProjectDefDto(WireModel):
    def_name: Union[str, None] = Field(default=None)
    label: Union[str, None] = Field(default=None)
    description: Union[str, None] = Field(default=None)
    cost: float = Field()
    prerequisites: Union[list[str], None] = Field(default=None)
    required_research_building: Union[str, None] = Field(default=None)
    tech_level: Union[str, None] = Field(default=None)
    tab: Union[str, None] = Field(default=None)
    hidden: bool = Field()
    unlocked_defs: Union[list[str], None] = Field(default=None)

class ResearchProjectDto(WireModel):
    name: Union[str, None] = Field(default=None)
    label: Union[str, None] = Field(default=None)
    progress: int = Field()
    research_points: int = Field()
    description: Union[str, None] = Field(default=None)
    is_finished: bool = Field()
    can_start_now: bool = Field()
    player_has_any_appropriate_research_bench: bool = Field()
    required_analyzed_thing_count: int = Field()
    analyzed_things_completed: int = Field()
    tech_level: Union[str, None] = Field(default=None)
    prerequisites: Union[list[str], None] = Field(default=None)
    hidden_prerequisites: Union[list[str], None] = Field(default=None)
    required_by_this: Union[list[str], None] = Field(default=None)
    progress_percent: float = Field()

class ResearchSummaryDto(WireModel):
    finished_projects_count: int = Field()
    total_projects_count: int = Field()
    available_projects_count: int = Field()
    by_tech_level: Union[dict[str, ResearchCategoryDto], None] = Field(default=None)
    by_tab: Union[dict[str, ResearchCategoryDto], None] = Field(default=None)

class ResearchTreeDto(WireModel):
    projects: Union[list[ResearchProjectDto], None] = Field(default=None)

class ResourceCategoryDto(WireModel):
    category: Union[str, None] = Field(default=None)
    count: int = Field()
    market_value: float = Field()

class ResourcesFoodSummaryDto(WireModel):
    unforbidden_nutrition: float = Field()
    forbidden_nutrition: float = Field()
    food_total: int = Field()
    total_nutrition: float = Field()
    meals_count: int = Field()
    raw_food_count: int = Field()
    rot_status_info: Union[RotStatusInfoDto, None] = Field(default=None)

class ResourcesSummaryDto(WireModel):
    total_items: int = Field()
    total_market_value: float = Field()
    last_updated: Union[str, None] = Field(default=None)
    categories: Union[list[ResourceCategoryDto], None] = Field(default=None)
    critical_resources: Union[CriticalResourcesDto, None] = Field(default=None)

class RoomDto(WireModel):
    visible_cell: Union[PositionDto, None] = Field(default=None)
    fogged_cells_count: int = Field()
    visible_cells: Union[list[PositionDto], None] = Field(default=None)
    cells_truncated: bool = Field()
    pawns_reaching_visible_cell: Union[list[int], None] = Field(default=None)
    id: int = Field()
    role_label: Union[str, None] = Field(default=None)
    temperature: float = Field()
    cells_count: int = Field()
    touches_map_edge: bool = Field()
    is_prison_cell: bool = Field()
    is_doorway: bool = Field()
    open_roof_count: int = Field()
    contained_beds_ids: Union[list[int], None] = Field(default=None)

class RoomRoleDefDto(WireModel):
    def_name: Union[str, None] = Field(default=None)
    label: Union[str, None] = Field(default=None)
    description: Union[str, None] = Field(default=None)

class RoomStatDefDto(WireModel):
    def_name: Union[str, None] = Field(default=None)
    label: Union[str, None] = Field(default=None)
    description: Union[str, None] = Field(default=None)
    score_stages: Union[list[RoomStatScoreStageDto], None] = Field(default=None)

class RoomStatScoreStageDto(WireModel):
    label: Union[str, None] = Field(default=None)
    min_score: float = Field()

class RotStatusInfoDto(WireModel):
    nutrition_rotating_soon: float = Field()
    nutrition_not_rotating: float = Field()
    percentage_rotating_soon: float = Field()
    soon_rotting_items: Union[list[RottingFoodItemDto], None] = Field(default=None)
    total_soon_rotting_items: int = Field()
    total_soon_rotting_stacks: int = Field()

class RottingFoodItemDto(WireModel):
    thing_id: int = Field()
    def_name: Union[str, None] = Field(default=None)
    label: Union[str, None] = Field(default=None)
    stack_count: int = Field()
    nutrition: float = Field()
    days_until_rot: float = Field()
    hours_until_rot: float = Field()

class SavedBuildingDto(WireModel):
    def_name: Union[str, None] = Field(default=None)
    stuff_def_name: Union[str, None] = Field(default=None)
    rel_x: int = Field()
    rel_z: int = Field()
    rotation: int = Field()

class SavedTerrainDto(WireModel):
    def_name: Union[str, None] = Field(default=None)
    rel_x: int = Field()
    rel_z: int = Field()

class SelectAreaRequestDto(WireModel):
    position_a: Union[PositionDto, None] = Field(default=None)
    position_b: Union[PositionDto, None] = Field(default=None)

class SendLetterRequestDto(WireModel):
    label: Union[str, None] = Field(default=None)
    message: Union[str, None] = Field(default=None)
    letter_def: Union[str, None] = Field(default=None)
    map_id: Union[str, None] = Field(default=None)
    look_target_thing_id: Union[str, None] = Field(default=None)
    faction_order_id: Union[str, None] = Field(default=None)
    hyperlink_thing_defs: Union[list[str], None] = Field(default=None)
    quest_id: Union[str, None] = Field(default=None)
    debug_info: Union[str, None] = Field(default=None)
    delay_ticks: int = Field()
    play_sound: bool = Field()

class ServerCacheResponseDto(WireModel):
    message: Union[str, None] = Field(default=None)

class SetForbiddenRequestDto(WireModel):
    thing_ids: Union[list[int], None] = Field(default=None)
    map_id: int = Field()
    forbidden: bool = Field()

class SettlementDto(WireModel):
    id: int = Field()
    name: Union[str, None] = Field(default=None)
    tile_id: int = Field()
    faction: Union[FactionDto, None] = Field(default=None)

class SiteDto(WireModel):
    id: int = Field()
    name: Union[str, None] = Field(default=None)
    tile_id: int = Field()
    faction: Union[FactionDto, None] = Field(default=None)

class SkillDefDto(WireModel):
    def_name: Union[str, None] = Field(default=None)
    label: Union[str, None] = Field(default=None)
    description: Union[str, None] = Field(default=None)
    passion_susceptible: bool = Field()
    disabling_work_tags: Union[str, None] = Field(default=None)

class SkillDto(WireModel):
    name: Union[str, None] = Field(default=None)
    level: int = Field()
    description: Union[str, None] = Field(default=None)
    min_level: int = Field()
    max_level: int = Field()
    level_descriptor: Union[str, None] = Field(default=None)
    permanently_disabled: bool = Field()
    totally_disabled: bool = Field()
    xp_total_earned: float = Field()
    xp_progress_percent: float = Field()
    xp_required_for_level_up: float = Field()
    xp_since_last_level: float = Field()
    aptitude: int = Field()
    passion: int = Field()
    disabled_work_tags: int = Field()

class SkillEntryDto(WireModel):
    skill_name: Union[str, None] = Field(default=None)
    level: Union[Union[int, None], None] = Field(default=None)
    passion: Union[Union[int, None], None] = Field(default=None)

class SkillRequirementDto(WireModel):
    skill: Union[str, None] = Field(default=None)
    min_level: int = Field()

class SocialInfoDto(WireModel):
    id: Union[str, None] = Field(default=None)
    name: Union[str, None] = Field(default=None)
    direct_relations: Union[list[RelationDto], None] = Field(default=None)
    children_count: int = Field()

class SoundDefDto(WireModel):
    def_name: Union[str, None] = Field(default=None)
    label: Union[str, None] = Field(default=None)
    max_simultaneous: int = Field()

class SpawnDropPodRequestDto(WireModel):
    map_id: int = Field()
    position: Union[PositionDto, None] = Field(default=None)
    items: Union[list[ThingDto], None] = Field(default=None)
    faction: Union[str, None] = Field(default=None)
    open_delay: bool = Field()

class SpawnItemRequestDto(WireModel):
    def_name: Union[str, None] = Field(default=None)
    stuff_def_name: Union[str, None] = Field(default=None)
    quality: Union[str, None] = Field(default=None)
    amount: int = Field()
    x: int = Field()
    z: int = Field()

class StatDefDto(WireModel):
    def_name: Union[str, None] = Field(default=None)
    label: Union[str, None] = Field(default=None)
    description: Union[str, None] = Field(default=None)
    category: Union[str, None] = Field(default=None)
    min_value: float = Field()
    max_value: float = Field()
    default_base_value: float = Field()
    show_on_pawns: bool = Field()
    show_on_humanlikes: bool = Field()
    show_on_animals: bool = Field()
    show_on_mechanoids: bool = Field()
    show_on_non_work_tables: bool = Field()
    always_allow_base_zero: bool = Field()

class StatModifierDto(WireModel):
    stat_def_name: Union[str, None] = Field(default=None)
    value: float = Field()

class StockRuleDto(WireModel):
    name: Union[str, None] = Field(default=None)
    count: Union[str, None] = Field(default=None)
    price: Union[str, None] = Field(default=None)
    buys: Union[bool, None] = Field(default=None)

class StockpileResponseDto(WireModel):
    zone_id: int = Field()
    name: Union[str, None] = Field(default=None)
    cells_count: int = Field()
    priority: int = Field()
    success: bool = Field()
    message: Union[str, None] = Field(default=None)

class StoragesSummaryDto(WireModel):
    total_stockpiles: int = Field()
    total_cells: int = Field()
    used_cells: int = Field()
    utilization_percent: int = Field()

class StorytellerDefDto(WireModel):
    def_name: Union[str, None] = Field(default=None)

class StreamConfigDto(WireModel):
    port: int = Field()
    address: Union[str, None] = Field(default=None)
    frame_width: int = Field()
    frame_height: int = Field()
    target_fps: int = Field()
    jpeg_quality: int = Field()

class StreamStatusDto(WireModel):
    is_streaming: bool = Field()
    config: Union[StreamConfigDto, None] = Field(default=None)

class StuffColorRequest(WireModel):
    name: Union[str, None] = Field(default=None)
    hex: Union[str, None] = Field(default=None)

class TerrainDefDto(WireModel):
    def_name: Union[str, None] = Field(default=None)
    label: Union[str, None] = Field(default=None)
    fertility: float = Field()
    path_cost: int = Field()
    extra_deterioration_factor: float = Field()
    layerable: bool = Field()
    affordances: Union[list[str], None] = Field(default=None)
    research_prerequisites: Union[list[str], None] = Field(default=None)

class ThingCostDto(WireModel):
    thing_def: Union[str, None] = Field(default=None)
    count: int = Field()

class ThingDefDto(WireModel):
    def_name: Union[str, None] = Field(default=None)
    label: Union[str, None] = Field(default=None)
    description: Union[str, None] = Field(default=None)
    category: Union[str, None] = Field(default=None)
    thing_class: Union[str, None] = Field(default=None)
    stat_base: Union[dict[str, float], None] = Field(default=None)
    cost_list: Union[list[ThingCostDto], None] = Field(default=None)
    is_weapon: bool = Field()
    is_apparel: bool = Field()
    is_item: bool = Field()
    is_pawn: bool = Field()
    is_plant: bool = Field()
    is_building: bool = Field()
    is_medicine: bool = Field()
    is_drug: bool = Field()
    market_value: float = Field()
    mass: float = Field()
    max_hit_points: float = Field()
    flammability: float = Field()
    stack_limit: int = Field()
    nutrition: float = Field()
    work_to_make: float = Field()
    work_to_build: float = Field()
    beauty: float = Field()
    tech_level: Union[str, None] = Field(default=None)
    trade_tags: Union[list[str], None] = Field(default=None)
    stuff_categories: Union[list[str], None] = Field(default=None)
    made_from_stuff: bool = Field()
    cost_stuff_count: int = Field()
    allowed_stuff_defs: Union[list[str], None] = Field(default=None)
    max_health: float = Field()
    armor_rating__sharp: float = Field()
    armor_rating__blunt: float = Field()
    armor_rating__heat: float = Field()
    insulation__cold: float = Field()
    insulation__heat: float = Field()

class ThingDto(WireModel):
    thing_id: int = Field()
    def_name: Union[str, None] = Field(default=None)
    label: Union[str, None] = Field(default=None)
    categories: Union[list[str], None] = Field(default=None)
    position: Union[PositionDto, None] = Field(default=None)
    rotation: int = Field()
    size: Union[PositionDto, None] = Field(default=None)
    stack_count: int = Field()
    market_value: float = Field()
    is_forbidden: bool = Field()
    quality: int = Field()
    stuff_def_name: Union[str, None] = Field(default=None)
    hit_points: int = Field()
    max_hit_points: int = Field()
    description: Union[str, None] = Field(default=None)

class ThingFilterDto(WireModel):
    allowed_thing_def_names: Union[list[str], None] = Field(default=None)
    disallowed_special_filter_def_names: Union[list[str], None] = Field(default=None)
    allowed_hit_points_min: float = Field()
    allowed_hit_points_max: float = Field()
    allowed_quality_min: Union[str, None] = Field(default=None)
    allowed_quality_max: Union[str, None] = Field(default=None)
    allowed_hit_points_configurable: bool = Field()
    allowed_qualities_configurable: bool = Field()

class ThingRecipeDto(WireModel):
    recipe_def_name: Union[str, None] = Field(default=None)
    label: Union[str, None] = Field(default=None)
    job_string: Union[str, None] = Field(default=None)
    work_amount: float = Field()
    work_time_seconds: float = Field()
    ingredients: Union[list[RecipeIngredientDto], None] = Field(default=None)
    produced_at: Union[list[RecipeProducerDto], None] = Field(default=None)
    skill_requirements: Union[list[RecipeSkillDto], None] = Field(default=None)
    research_prerequisite: Union[str, None] = Field(default=None)

class ThingSourcesDto(WireModel):
    def_name: Union[str, None] = Field(default=None)
    label: Union[str, None] = Field(default=None)
    thing_categories: Union[list[str], None] = Field(default=None)
    can_craft: bool = Field()
    can_trade: bool = Field()
    can_harvest: bool = Field()
    can_mine: bool = Field()
    can_butcher: bool = Field()
    crafting_recipes: Union[list[str], None] = Field(default=None)
    harvested_from: Union[list[str], None] = Field(default=None)
    mined_from: Union[list[str], None] = Field(default=None)
    trade_tags: Union[list[str], None] = Field(default=None)

class ThingsAtCellRequestDto(WireModel):
    map_id: int = Field()
    position: Union[PositionDto, None] = Field(default=None)

class ThoughtDefDto(WireModel):
    def_name: Union[str, None] = Field(default=None)
    label: Union[str, None] = Field(default=None)
    description: Union[str, None] = Field(default=None)
    mood_offset: float = Field()
    duration_days: float = Field()
    stack_limit: int = Field()
    stages: Union[list[ThoughtStageDto], None] = Field(default=None)

class ThoughtStageDto(WireModel):
    label: Union[str, None] = Field(default=None)
    description: Union[str, None] = Field(default=None)
    base_mood_effect: float = Field()
    base_opinion_offset: float = Field()

class TileDetailsDto(WireModel):
    time_zone: Union[str, None] = Field(default=None)
    forageability: float = Field()
    growing_period: Union[str, None] = Field(default=None)
    movement_difficulty: float = Field()
    stone_types: Union[list[str], None] = Field(default=None)
    id: int = Field()
    biome: Union[str, None] = Field(default=None)
    elevation: float = Field()
    temperature: float = Field()
    rainfall: float = Field()
    hilliness: Union[str, None] = Field(default=None)
    roads: Union[list[str], None] = Field(default=None)
    rivers: Union[list[str], None] = Field(default=None)
    lat: float = Field()
    lon: float = Field()
    is_polluted: bool = Field()
    pollution: float = Field()

class TileDto(WireModel):
    id: int = Field()
    biome: Union[str, None] = Field(default=None)
    elevation: float = Field()
    temperature: float = Field()
    rainfall: float = Field()
    hilliness: Union[str, None] = Field(default=None)
    roads: Union[list[str], None] = Field(default=None)
    rivers: Union[list[str], None] = Field(default=None)
    lat: float = Field()
    lon: float = Field()
    is_polluted: bool = Field()
    pollution: float = Field()

class TimeAssignmentDto(WireModel):
    name: Union[str, None] = Field(default=None)

class TraderKindDto(WireModel):
    def_name: Union[str, None] = Field(default=None)
    label: Union[str, None] = Field(default=None)
    orbital: bool = Field()
    visitor: bool = Field()
    commonality: float = Field()
    items: Union[list[StockRuleDto], None] = Field(default=None)
    categories: Union[list[StockRuleDto], None] = Field(default=None)
    tags: Union[list[StockRuleDto], None] = Field(default=None)
    special: Union[list[StockRuleDto], None] = Field(default=None)

class TraitDefDto(WireModel):
    def_name: Union[str, None] = Field(default=None)
    label: Union[str, None] = Field(default=None)
    description: Union[str, None] = Field(default=None)
    degree_datas: Union[list[TraitDegreeDto], None] = Field(default=None)
    conflicting_traits: Union[list[str], None] = Field(default=None)
    disabled_work_types: Union[list[str], None] = Field(default=None)
    disabled_work_tags: Union[str, None] = Field(default=None)

class TraitDegreeDto(WireModel):
    label: Union[str, None] = Field(default=None)
    description: Union[str, None] = Field(default=None)
    degree: int = Field()
    skill_gains: Union[dict[str, int], None] = Field(default=None)
    stat_offsets: Union[list[StatModifierDto], None] = Field(default=None)
    stat_factors: Union[list[StatModifierDto], None] = Field(default=None)

class TraitDto(WireModel):
    name: Union[str, None] = Field(default=None)
    label: Union[str, None] = Field(default=None)
    description: Union[str, None] = Field(default=None)
    disabled_work_tags: int = Field()
    suppressed: bool = Field()

class TraitEntryDto(WireModel):
    trait_name: Union[str, None] = Field(default=None)
    degree: Union[Union[int, None], None] = Field(default=None)

class TriggerIncidentRequestDto(WireModel):
    name: Union[str, None] = Field(default=None)
    map_id: Union[str, None] = Field(default=None)
    incident_parms: Union[IncidentParmsDto, None] = Field(default=None)

class UI_AlertDto(WireModel):
    label: Union[str, None] = Field(default=None)
    explanation: Union[str, None] = Field(default=None)
    priority: Union[str, None] = Field(default=None)
    targets: Union[list[int], None] = Field(default=None)
    cells: Union[list[str], None] = Field(default=None)

class UpdateBillRequest(WireModel):
    repeat_mode: Union[str, None] = Field(default=None)
    repeat_count: Union[Union[int, None], None] = Field(default=None)
    target_count: Union[Union[int, None], None] = Field(default=None)
    store_mode: Union[str, None] = Field(default=None)
    suspended: Union[Union[bool, None], None] = Field(default=None)
    pause_when_satisfied: Union[Union[bool, None], None] = Field(default=None)
    unpause_when_you_have: Union[Union[int, None], None] = Field(default=None)
    include_equipped: Union[Union[bool, None], None] = Field(default=None)
    include_tainted: Union[Union[bool, None], None] = Field(default=None)
    hp_range: Union[FloatRange, None] = Field(default=None)
    quality_range: Union[IntRangeDto, None] = Field(default=None)
    limit_to_allowed_stuff: Union[Union[bool, None], None] = Field(default=None)
    ingredient_search_radius: Union[Union[float, None], None] = Field(default=None)
    allowed_skill_range: Union[IntRangeDto, None] = Field(default=None)
    pawn_restriction_id: Union[Union[int, None], None] = Field(default=None)
    player_custom_name: Union[str, None] = Field(default=None)
    allowed_materials: Union[list[str], None] = Field(default=None)

class UpdateStockpileRequestDto(WireModel):
    zone_id: int = Field()
    name: Union[str, None] = Field(default=None)
    priority: Union[Union[int, None], None] = Field(default=None)
    add_item_defs: Union[list[str], None] = Field(default=None)
    remove_item_defs: Union[list[str], None] = Field(default=None)
    add_item_categories: Union[list[str], None] = Field(default=None)
    remove_item_categories: Union[list[str], None] = Field(default=None)
    min_hit_points_percent: Union[Union[float, None], None] = Field(default=None)
    max_hit_points_percent: Union[Union[float, None], None] = Field(default=None)
    min_quality: Union[str, None] = Field(default=None)
    max_quality: Union[str, None] = Field(default=None)

class VersionDto(WireModel):
    version: Union[str, None] = Field(default=None)
    rim_world_version: Union[str, None] = Field(default=None)
    mod_version: Union[str, None] = Field(default=None)
    api_version: Union[str, None] = Field(default=None)

class WeatherDefDto(WireModel):
    def_name: Union[str, None] = Field(default=None)
    label: Union[str, None] = Field(default=None)
    description: Union[str, None] = Field(default=None)
    min_temperature: float = Field()
    max_temperature: float = Field()
    wind_speed_factor: float = Field()
    move_speed_multiplier: float = Field()
    accuracy_multiplier: float = Field()

class WindowCloseRequestDto(WireModel):
    window_types: Union[list[str], None] = Field(default=None)
    force_pause_only: bool = Field()

class WindowCloseResultDto(WireModel):
    closed_count: int = Field()
    closed_windows: Union[list[str], None] = Field(default=None)

class WindowDialogRequestDto(WireModel):
    title: Union[str, None] = Field(default=None)
    text: Union[str, None] = Field(default=None)
    options: Union[list[DialogOptionDto], None] = Field(default=None)

class WindowMessageRequestDto(WireModel):
    title: Union[str, None] = Field(default=None)
    text: Union[str, None] = Field(default=None)
    button_text: Union[str, None] = Field(default=None)

class WorkInfoDto(WireModel):
    skills: Union[list[SkillDto], None] = Field(default=None)
    current_job: Union[str, None] = Field(default=None)
    traits: Union[list[TraitDto], None] = Field(default=None)
    work_priorities: Union[list[WorkPriorityDto], None] = Field(default=None)

class WorkListDto(WireModel):
    work: Union[list[str], None] = Field(default=None)

class WorkPriorityDto(WireModel):
    work_type: Union[str, None] = Field(default=None)
    priority: int = Field()
    is_totally_disabled: bool = Field()

class WorkPriorityRequestDto(WireModel):
    id: int = Field()
    work: Union[str, None] = Field(default=None)
    priority: int = Field()

class WorkTableDto(WireModel):
    id: int = Field()
    thing_def: Union[str, None] = Field(default=None)
    label: Union[str, None] = Field(default=None)
    position: Union[PositionDto, None] = Field(default=None)
    bills_count: int = Field()

class WorkTypeDefDto(WireModel):
    def_name: Union[str, None] = Field(default=None)
    label: Union[str, None] = Field(default=None)
    description: Union[str, None] = Field(default=None)
    work_tags: Union[str, None] = Field(default=None)
    relevant_skills: Union[list[str], None] = Field(default=None)
    natural_priority: int = Field()
    visible_only_with_work: bool = Field()

class WorldObjectDefDto(WireModel):
    def_name: Union[str, None] = Field(default=None)
    label: Union[str, None] = Field(default=None)
    description: Union[str, None] = Field(default=None)
    can_have_faction: bool = Field()
    selectable: bool = Field()
    never_multi_select: bool = Field()
    expanding_icon: bool = Field()
    can_be_randomly_placed: bool = Field()

class ZoneDto(WireModel):
    id: int = Field()
    cells_count: int = Field()
    label: Union[str, None] = Field(default=None)
    base_label: Union[str, None] = Field(default=None)
    type: Union[str, None] = Field(default=None)

class WorkSettings(WireModel):
    use_work_priorities: bool = Field()

class ResourceCell(WireModel):
    x: int = Field()
    z: int = Field()

class ResourceLocation(WireModel):
    nearby_count: int = Field()
    nearest_distance: float = Field()
    nearest_cell: ResourceCell = Field()
    sample_ids: list[int] = Field()

class TerrainResource(WireModel):
    def_name: str = Field()
    label: str = Field()
    fertility: float = Field()
    visible_cells: int = Field()
    nearby_cells: int = Field()
    largest_nearby_patch: int = Field()
    nearest_cell: ResourceCell = Field()
    nearest_distance: float = Field()

class PlantResource(WireModel):
    def_name: str = Field()
    label: str = Field()
    harvest_product: Union[str, None] = Field()
    wild_count: int = Field()
    sown_count: int = Field()
    harvestable_count: int = Field()
    harvestable_yield: int = Field()
    nearby_harvestable_yield: int = Field()
    location: ResourceLocation = Field()

class AnimalResource(WireModel):
    def_name: str = Field()
    label: str = Field()
    count: int = Field()
    wild_count: int = Field()
    owned_count: int = Field()
    hostile_count: int = Field()
    wild_meat_estimate: float = Field()
    nearby_wild_meat_estimate: float = Field()
    meat_def: Union[str, None] = Field()
    predator: bool = Field()
    manhunter_on_damage_chance: float = Field()
    location: ResourceLocation = Field()

class MineralResource(WireModel):
    def_name: str = Field()
    label: str = Field()
    product_def: str = Field()
    visible_blocks: int = Field()
    base_yield_estimate: int = Field()
    nearby_base_yield_estimate: int = Field()
    location: ResourceLocation = Field()

class SupplyResource(WireModel):
    def_name: str = Field()
    label: str = Field()
    quantity: int = Field()
    allowed_quantity: int = Field()
    forbidden_quantity: int = Field()
    nearby_allowed_quantity: int = Field()
    nearby_forbidden_quantity: int = Field()
    nutrition_per_unit: float = Field()
    location: ResourceLocation = Field()

class CropResource(WireModel):
    def_name: str = Field()
    label: str = Field()
    product_def: str = Field()
    min_fertility: float = Field()
    base_grow_days: float = Field()
    min_sow_skill: int = Field()
    base_harvest_yield: float = Field()
    nutrition_per_product: float = Field()
    growth_season_now: bool = Field()
    nearby_fertility_eligible_cells: int = Field()

class FishingResource(WireModel):
    nearest_cell: ResourceCell = Field()
    nearest_distance: float = Field()
    visible_water_cells: int = Field()
    nearby_water_cells: int = Field()
    has_fish: bool = Field()
    population: float = Field()
    max_population: float = Field()
    totally_frozen: Union[bool, None] = Field()
    pollution_fraction: float = Field()
    fish_defs: list[str] = Field()

class MapResourceOverview(WireModel):
    map_id: int = Field()
    observed_tick: int = Field()
    terrain_observed_tick: int = Field()
    terrain_cache_hit: bool = Field()
    terrain_max_age_ticks: int = Field()
    center: ResourceCell = Field()
    nearby_radius: int = Field()
    visible_cells: int = Field()
    unexplored_cells: int = Field()
    outdoor_temperature: float = Field()
    seasonal_temperature: float = Field()
    fishing_available: bool = Field()
    terrain: list[TerrainResource] = Field()
    plants: list[PlantResource] = Field()
    animals: list[AnimalResource] = Field()
    minerals: list[MineralResource] = Field()
    supplies: list[SupplyResource] = Field()
    food_crops: list[CropResource] = Field()
    fishing: list[FishingResource] = Field()
    notes: list[str] = Field()

class ConstructionWorkerObservation(WireModel):
    pawn_id: int = Field()
    name: str = Field()
    idle: bool = Field()
    drafted: bool = Field()
    downed: bool = Field()
    construction_priority: int = Field()
    current_job: str = Field()
    target_ids: list[int] = Field()

class ConstructionWorkerCheck(WireModel):
    pawn_id: int = Field()
    can_construct: bool = Field()
    reason: str = Field()
    blocking_thing_id: Union[int, None] = Field()

class ConstructionMaterialObservation(WireModel):
    def_name: str = Field()
    needed: int = Field()
    allowed_quantity: int = Field()
    forbidden_quantity: int = Field()
    accessible_quantity: int = Field()
    accessible_sample_ids: list[int] = Field()

class ConstructionWorkSite(WireModel):
    thing_id: int = Field()
    def_name: str = Field()
    position: ResourceCell = Field()
    stage: str = Field()
    work_done: float = Field()
    work_total: float = Field()
    targeted_by: list[int] = Field()
    workers: list[ConstructionWorkerCheck] = Field()
    materials: list[ConstructionMaterialObservation] = Field()

class ConstructionWorkOverview(WireModel):
    map_id: int = Field()
    observed_tick: int = Field()
    total: int = Field()
    offset: int = Field()
    next_offset: Union[int, None] = Field()
    workers: list[ConstructionWorkerObservation] = Field()
    sites: list[ConstructionWorkSite] = Field()
    notes: list[str] = Field()

class Construction_DefinitionQuery(WireModel):
    search: str = Field()
    offset: int = Field(ge=0, le=2147483647)
    limit: int = Field(ge=1, le=32)

class Construction_Cost(WireModel):
    def_name: str = Field()
    count: int = Field()

class Construction_Material(WireModel):
    def_name: str = Field()
    label: str = Field()

class Construction_BuildingDefinition(WireModel):
    def_name: str = Field()
    label: str = Field()
    size_x: int = Field()
    size_z: int = Field()
    rotatable: bool = Field()
    work_to_build: float = Field()
    stuff_count: int = Field()
    costs: list[Construction_Cost] = Field()
    allowed_materials: list[Construction_Material] = Field()
    description: str = Field()
    is_bed: bool = Field()
    bed_humanlike: bool = Field()

class Construction_PageBuildingDefinition(WireModel):
    items: list[Construction_BuildingDefinition] = Field()
    total: int = Field()
    next_offset: Union[int, None] = Field()

class Construction_RoomQuery(WireModel):
    map_id: int = Field()
    offset: int = Field(ge=0, le=2147483647)
    limit: int = Field(ge=1, le=32)
    near: Construction_Cell = Field()
    cell_limit: int = Field(ge=1, le=256)

class Construction_PositionDto(WireModel):
    x: int = Field()
    y: int = Field()
    z: int = Field()

class Construction_RoomDto(WireModel):
    visible_cell: Union[Construction_PositionDto, None] = Field()
    fogged_cells_count: int = Field()
    visible_cells: list[Construction_PositionDto] = Field()
    cells_truncated: bool = Field()
    pawns_reaching_visible_cell: list[int] = Field()
    id: int = Field()
    role_label: str = Field()
    temperature: float = Field()
    cells_count: int = Field()
    touches_map_edge: bool = Field()
    is_prison_cell: bool = Field()
    is_doorway: bool = Field()
    open_roof_count: int = Field()
    contained_beds_ids: list[int] = Field()

class Construction_PageRoomDto(WireModel):
    items: list[Construction_RoomDto] = Field()
    total: int = Field()
    next_offset: Union[int, None] = Field()

class Construction_Cell(WireModel):
    x: int = Field()
    z: int = Field()

class Construction_Placement(WireModel):
    def_name: str = Field()
    stuff_def_name: str = Field()
    position: Construction_Cell = Field()
    rotation: int = Field(ge=0, le=3)

class Construction_ConstructionRequest(WireModel):
    map_id: int = Field()
    buildings: list[Construction_Placement] = Field(min_length=1, max_length=128)

class Construction_PlacementResult(WireModel):
    placement: Construction_Placement = Field()
    state: Literal['ready', 'blueprint', 'frame', 'built', 'rejected'] = Field()
    thing_id: Union[int, None] = Field()
    reason: str = Field()

class Construction_ConstructionResult(WireModel):
    accepted: bool = Field()
    items: list[Construction_PlacementResult] = Field()

class Construction_ContractError(WireModel):
    code: str = Field()
    message: str = Field()

class Construction_MapQuery(WireModel):
    map_id: int = Field()

class Construction_ConstructionThing(WireModel):
    thing_id: int = Field()
    def_name: str = Field()
    position: Construction_Cell = Field()
    rotation: int = Field()
    state: str = Field()

class Construction_ConstructionState(WireModel):
    revision: str = Field()
    buildings: list[Construction_ConstructionThing] = Field()

class Construction_AreaQuery(WireModel):
    map_id: int = Field()
    center: Construction_Cell = Field()
    radius: int = Field(ge=0, le=12)

class Construction_AreaCell(WireModel):
    position: Construction_Cell = Field()
    terrain_def: str = Field()
    fertility: float = Field()
    roofed: bool = Field()
    walkable: bool = Field()
    thing_ids: list[int] = Field()

class Construction_AreaResult(WireModel):
    center: Construction_Cell = Field()
    radius: int = Field()
    cells: list[Construction_AreaCell] = Field()

class Construction_AllowAllRequest(WireModel):
    map_id: int = Field()

class Construction_AllowAllResult(WireModel):
    map_id: int = Field()
    changed_count: int = Field()
    excluded_jelly_count: int = Field()
    remaining_eligible_count: int = Field()

class get_api_openapi_json_Query(WireModel):
    pass

class post_v1_builder_blueprint_Query(WireModel):
    pass

class post_v1_builder_check_zone_Query(WireModel):
    pass

class post_v1_builder_copy_Query(WireModel):
    pass

class post_v1_builder_paste_Query(WireModel):
    pass

class get_v1_buildings_bill_Query(WireModel):
    building_id: int = Field()
    bill_id: int = Field()

class delete_v1_buildings_bill_remove_Query(WireModel):
    building_id: int = Field()
    bill_id: int = Field()

class put_v1_buildings_bill_reorder_Query(WireModel):
    building_id: int = Field()
    bill_id: int = Field()

class put_v1_buildings_bill_suspend_Query(WireModel):
    building_id: int = Field()
    bill_id: int = Field()

class put_v1_buildings_bill_update_Query(WireModel):
    building_id: int = Field()
    bill_id: int = Field()

class get_v1_buildings_bills_Query(WireModel):
    building_id: int = Field()

class post_v1_buildings_bills_add_Query(WireModel):
    building_id: int = Field()

class delete_v1_buildings_bills_remove_Query(WireModel):
    building_id: int = Field()

class get_v1_buildings_recipes_Query(WireModel):
    building_id: int = Field()
    only_researched: Union[bool, None] = Field(default=None)

class post_v1_cache_clear_Query(WireModel):
    pass

class post_v1_cache_disable_Query(WireModel):
    pass

class post_v1_cache_enable_Query(WireModel):
    pass

class get_v1_cache_status_Query(WireModel):
    detailed: Union[bool, None] = Field(default=None)

class post_v1_camera_change_position_Query(WireModel):
    x: int = Field()
    y: int = Field()

class post_v1_camera_change_zoom_Query(WireModel):
    zoom: int = Field()

class post_v1_camera_follow_pawn_Query(WireModel):
    pawn_id: int = Field()

class post_v1_camera_screenshot_Query(WireModel):
    pass

class post_v1_camera_screenshot_native_Query(WireModel):
    pass

class post_v1_camera_stream_setup_Query(WireModel):
    pass

class post_v1_camera_stream_start_Query(WireModel):
    pass

class get_v1_camera_stream_status_Query(WireModel):
    pass

class post_v1_camera_stream_stop_Query(WireModel):
    pass

class get_v1_client_learning_active_Query(WireModel):
    pass

class get_v1_client_learning_all_Query(WireModel):
    pass

class get_v1_client_learning_concept_Query(WireModel):
    def_name: str = Field()

class get_v1_client_learning_defs_Query(WireModel):
    pass

class post_v1_client_learning_mark_learned_Query(WireModel):
    pass

class get_v1_colonist_Query(WireModel):
    id: int = Field()

class get_v1_colonist_body_image_Query(WireModel):
    id: int = Field()

class get_v1_colonist_detailed_Query(WireModel):
    id: int = Field()

class get_v1_colonist_inventory_Query(WireModel):
    id: int = Field()

class get_v1_colonist_opinion_about_Query(WireModel):
    id: int = Field()
    other_id: int = Field()

class post_v1_colonist_time_assignment_Query(WireModel):
    pawn_id: Union[int, None] = Field(default=None)
    hour: Union[int, None] = Field(default=None)
    assignment: Union[str, None] = Field(default=None)

class post_v1_colonist_work_priority_Query(WireModel):
    pass

class get_v1_colonists_Query(WireModel):
    pass

class get_v1_colonists_detailed_Query(WireModel):
    pass

class get_v1_colonists_positions_Query(WireModel):
    pass

class post_v1_colonists_work_priority_Query(WireModel):
    pass

class get_v1_core_docs_export_Query(WireModel):
    format: Union[Literal['json', 'markdown'], None] = Field(default=None)

class get_v1_datetime_Query(WireModel):
    pass

class get_v1_datetime_tile_Query(WireModel):
    tile_id: int = Field()

class get_v1_def_all_Query(WireModel):
    include: Union[str, None] = Field(default=None)
    filter: Union[str, None] = Field(default=None)

class post_v1_deselect_Query(WireModel):
    pass

class post_v1_dev_console_Query(WireModel):
    action: Union[str, None] = Field(default=None)
    message: Union[str, None] = Field(default=None)

class get_v1_dev_endpoints_Query(WireModel):
    pass

class get_v1_dev_materials_atlas_Query(WireModel):
    pass

class post_v1_dev_materials_atlas_clear_Query(WireModel):
    pass

class post_v1_dev_stuff_color_Query(WireModel):
    pass

class get_v1_docs_Query(WireModel):
    format: Union[Literal['html', 'json', 'markdown'], None] = Field(default=None)

class get_v1_docs_health_Query(WireModel):
    pass

class get_v1_events_Query(WireModel):
    pass

class get_v1_faction_Query(WireModel):
    id: int = Field()

class post_v1_faction_change_goodwill_Query(WireModel):
    pass

class get_v1_faction_def_Query(WireModel):
    name: str = Field()

class post_v1_faction_goodwill_Query(WireModel):
    pass

class get_v1_faction_icon_Query(WireModel):
    id: int = Field()

class get_v1_faction_player_Query(WireModel):
    pass

class get_v1_faction_relation_with_Query(WireModel):
    id: int = Field()
    other_id: int = Field()

class get_v1_faction_relations_Query(WireModel):
    id: int = Field()

class get_v1_factions_Query(WireModel):
    pass

class get_v1_game_defs_interactions_Query(WireModel):
    pass

class post_v1_game_load_Query(WireModel):
    pass

class post_v1_game_main_menu_Query(WireModel):
    pass

class post_v1_game_quit_Query(WireModel):
    pass

class post_v1_game_save_Query(WireModel):
    pass

class post_v1_game_select_area_Query(WireModel):
    pass

class post_v1_game_send_letter_Query(WireModel):
    pass

class get_v1_game_settings_Query(WireModel):
    pass

class get_v1_game_settings_run_in_background_Query(WireModel):
    pass

class post_v1_game_settings_toggle_run_in_background_Query(WireModel):
    pass

class post_v1_game_speed_Query(WireModel):
    speed: int = Field()

class post_v1_game_start_Query(WireModel):
    pass

class post_v1_game_start_devquick_Query(WireModel):
    pass

class get_v1_game_state_Query(WireModel):
    pass

class get_v1_incident_chance_Query(WireModel):
    pass

class post_v1_incident_trigger_Query(WireModel):
    pass

class get_v1_incidents_Query(WireModel):
    map_id: int = Field()

class get_v1_incidents_top_Query(WireModel):
    pass

class post_v1_item_change_image_Query(WireModel):
    pass

class get_v1_item_image_Query(WireModel):
    name: str = Field()

class get_v1_item_recipes_Query(WireModel):
    def_name: str = Field()

class get_v1_item_sources_Query(WireModel):
    def_name: str = Field()

class post_v1_item_spawn_Query(WireModel):
    pass

class post_v1_jobs_make_equip_Query(WireModel):
    map_id: int = Field()
    pawn_id: int = Field()
    item_id: int = Field()

class get_v1_lords_Query(WireModel):
    map_id: int = Field()

class post_v1_lords_create_Query(WireModel):
    pass

class get_v1_map_animals_Query(WireModel):
    map_id: int = Field()

class get_v1_map_building_info_Query(WireModel):
    map_id: int = Field()

class post_v1_map_building_power_Query(WireModel):
    buildingId: int = Field()
    powerOn: Union[bool, None] = Field(default=None)

class get_v1_map_buildings_Query(WireModel):
    map_id: int = Field()

class get_v1_map_creatures_summary_Query(WireModel):
    map_id: int = Field()

class post_v1_map_destroy_corpses_Query(WireModel):
    map_id: int = Field()

class post_v1_map_destroy_forbidden_Query(WireModel):
    map_id: int = Field()

class post_v1_map_destroy_rect_Query(WireModel):
    pass

class post_v1_map_droppod_Query(WireModel):
    pass

class get_v1_map_farm_summary_Query(WireModel):
    map_id: int = Field()

class get_v1_map_fog_grid_Query(WireModel):
    map_id: int = Field()

class get_v1_map_ore_Query(WireModel):
    map_id: int = Field()

class get_v1_map_pawns_Query(WireModel):
    map_id: int = Field()

class get_v1_map_plants_Query(WireModel):
    map_id: int = Field()

class get_v1_map_power_info_Query(WireModel):
    map_id: int = Field()

class post_v1_map_repair_positions_Query(WireModel):
    pass

class post_v1_map_repair_rect_Query(WireModel):
    pass

class get_v1_map_rooms_Query(WireModel):
    map_id: int = Field()

class get_v1_map_terrain_Query(WireModel):
    map_id: int = Field()

class get_v1_map_things_Query(WireModel):
    map_id: int = Field()

class get_v1_map_things_at_Query(WireModel):
    pass

class get_v1_map_things_radius_Query(WireModel):
    map_id: int = Field()
    x: int = Field()
    z: int = Field()
    radius: int = Field()

class get_v1_map_weather_Query(WireModel):
    map_id: int = Field()

class post_v1_map_weather_change_Query(WireModel):
    map_id: int = Field()
    name: str = Field()

class get_v1_map_work_tables_Query(WireModel):
    map_id: int = Field()

class get_v1_map_zone_growing_Query(WireModel):
    map_id: int = Field()
    zone_id: int = Field()

class post_v1_map_zone_growing_Query(WireModel):
    pass

class post_v1_map_zone_stockpile_Query(WireModel):
    pass

class delete_v1_map_zone_stockpile_delete_Query(WireModel):
    zone_id: int = Field()

class post_v1_map_zone_stockpile_update_Query(WireModel):
    pass

class get_v1_map_zones_Query(WireModel):
    map_id: int = Field()

class get_v1_maps_Query(WireModel):
    pass

class post_v1_mods_configure_Query(WireModel):
    pass

class get_v1_mods_info_Query(WireModel):
    package_id: str = Field()
    uid: str = Field()

class get_v1_mods_list_Query(WireModel):
    pass

class get_v1_mods_preview_Query(WireModel):
    package_id: str = Field()
    uid: str = Field()

class post_v1_open_tab_Query(WireModel):
    name: str = Field()

class post_v1_order_designate_area_Query(WireModel):
    pass

class get_v1_outfits_Query(WireModel):
    pass

class post_v1_pawn_edit_apparel_Query(WireModel):
    pass

class post_v1_pawn_edit_basic_Query(WireModel):
    pass

class post_v1_pawn_edit_faction_Query(WireModel):
    pass

class post_v1_pawn_edit_health_Query(WireModel):
    pass

class post_v1_pawn_edit_inventory_Query(WireModel):
    pass

class post_v1_pawn_edit_needs_Query(WireModel):
    pass

class post_v1_pawn_edit_position_Query(WireModel):
    pass

class post_v1_pawn_edit_skills_Query(WireModel):
    pass

class post_v1_pawn_edit_status_Query(WireModel):
    pass

class post_v1_pawn_edit_traits_Query(WireModel):
    pass

class post_v1_pawn_job_Query(WireModel):
    pass

class post_v1_pawn_medical_bed_rest_Query(WireModel):
    pass

class post_v1_pawn_medical_tend_Query(WireModel):
    pass

class get_v1_pawn_portrait_image_Query(WireModel):
    pawn_id: int = Field()
    width: int = Field()
    height: int = Field()
    direction: str = Field()

class post_v1_pawn_spawn_Query(WireModel):
    pass

class get_v1_pawns_details_Query(WireModel):
    id: int = Field()

class get_v1_pawns_interactions_Query(WireModel):
    pawn_id: int = Field()

class post_v1_pawns_interactions_force_Query(WireModel):
    pass

class get_v1_pawns_interactions_log_Query(WireModel):
    pawn_id: int = Field()
    limit: int = Field()

class get_v1_pawns_inventory_Query(WireModel):
    id: int = Field()

class get_v1_pawns_opinions_Query(WireModel):
    pawn_id: int = Field()

class get_v1_pawns_relations_Query(WireModel):
    pawn_id: int = Field()

class post_v1_pawns_relations_add_Query(WireModel):
    pass

class delete_v1_pawns_relations_remove_Query(WireModel):
    pawn1_id: int = Field()
    pawn2_id: int = Field()
    relation_def_name: str = Field()

class get_v1_quests_Query(WireModel):
    map_id: int = Field()

class get_v1_research_finished_Query(WireModel):
    pass

class get_v1_research_progress_Query(WireModel):
    pass

class get_v1_research_project_Query(WireModel):
    name: str = Field()

class post_v1_research_stop_Query(WireModel):
    pass

class get_v1_research_summary_Query(WireModel):
    pass

class post_v1_research_target_Query(WireModel):
    name: str = Field()
    force: Union[bool, None] = Field(default=None)

class get_v1_research_tree_Query(WireModel):
    pass

class get_v1_resources_storages_summary_Query(WireModel):
    map_id: int = Field()

class get_v1_resources_stored_Query(WireModel):
    map_id: int = Field()
    category: Union[str, None] = Field(default=None)

class get_v1_resources_summary_Query(WireModel):
    map_id: int = Field()

class post_v1_select_Query(WireModel):
    type: str = Field()
    id: int = Field()

class get_v1_terrain_image_Query(WireModel):
    name: str = Field()

class post_v1_things_set_forbidden_Query(WireModel):
    pass

class get_v1_time_assignments_Query(WireModel):
    pass

class get_v1_traders_defs_Query(WireModel):
    pass

class get_v1_trait_def_Query(WireModel):
    name: str = Field()

class get_v1_ui_alerts_Query(WireModel):
    pass

class post_v1_ui_announce_Query(WireModel):
    pass

class post_v1_ui_dialog_Query(WireModel):
    pass

class post_v1_ui_message_Query(WireModel):
    pass

class post_v1_ui_window_close_Query(WireModel):
    pass

class get_v1_ui_windows_Query(WireModel):
    pass

class get_v1_version_Query(WireModel):
    pass

class get_v1_work_list_Query(WireModel):
    pass

class get_v1_world_caravan_path_Query(WireModel):
    id: int = Field()

class get_v1_world_caravans_Query(WireModel):
    pass

class get_v1_world_grid_Query(WireModel):
    pass

class get_v1_world_grid_area_Query(WireModel):
    tile_id: int = Field()
    radius: float = Field()

class get_v1_world_player_settlements_Query(WireModel):
    pass

class get_v1_world_settlements_Query(WireModel):
    pass

class get_v1_world_sites_Query(WireModel):
    pass

class get_v1_world_tile_Query(WireModel):
    id: int = Field()

class get_v1_world_tile_coordinates_Query(WireModel):
    id: int = Field()

class get_v1_world_tile_details_Query(WireModel):
    id: int = Field()

class get_v2_colonist_detailed_Query(WireModel):
    id: int = Field()

class get_v2_colonists_detailed_Query(WireModel):
    pass

class get_api_v2_construction_contracts_Query(WireModel):
    pass

class construction_definitions_Query(WireModel):
    pass

class construction_inspect_Query(WireModel):
    pass

class get_api_v2_construction_openapi_Query(WireModel):
    pass

class construction_place_Query(WireModel):
    expected_revision: Union[str, None] = Field(default=None)

class construction_rooms_Query(WireModel):
    pass

class construction_state_Query(WireModel):
    pass

class construction_area_Query(WireModel):
    pass

class get_v1_work_settings_Query(WireModel):
    pass

class post_v1_work_settings_Query(WireModel):
    pass

class get_v1_map_resource_overview_Query(WireModel):
    map_id: int = Field()
    center_x: int = Field()
    center_z: int = Field()
    nearby_radius: Union[int, None] = Field(default=None, ge=1, le=100)
    refresh_terrain: Union[bool, None] = Field(default=None)

class get_v1_map_construction_work_Query(WireModel):
    map_id: int = Field()
    offset: Union[int, None] = Field(default=None, ge=0)
    limit: Union[int, None] = Field(default=None, ge=1, le=32)

class orders_unforbid_all_Query(WireModel):
    pass

class orders_forbidden_overview_Query(WireModel):
    pass

AbilityDefDto.model_rebuild()
AddRelationRequestDto.model_rebuild()
AllDefsRequestDto.model_rebuild()
AnimalDefDto.model_rebuild()
AnimalDto.model_rebuild()
ApiResult.model_rebuild()
ApiResult_Anonymousb7f0d20d59.model_rebuild()
ApiResult_Anonymouse219319f76.model_rebuild()
ApiResult_ApiV1PawnDetailedDto.model_rebuild()
ApiResult_BillDto.model_rebuild()
ApiResult_BlueprintDto.model_rebuild()
ApiResult_BodyPartsDto.model_rebuild()
ApiResult_BuildingDto.model_rebuild()
ApiResult_Camera_CameraScreenshotResponseDto.model_rebuild()
ApiResult_CaravanPathDto.model_rebuild()
ApiResult_CheckZoneResultDto.model_rebuild()
ApiResult_CoordinatesDto.model_rebuild()
ApiResult_Core_ApiDocumentation.model_rebuild()
ApiResult_DefsDto.model_rebuild()
ApiResult_Dictionary_string__List_ThingDto.model_rebuild()
ApiResult_EndpointListDto.model_rebuild()
ApiResult_FactionChangeRelationResponceDto.model_rebuild()
ApiResult_FactionDefDto.model_rebuild()
ApiResult_FactionDto.model_rebuild()
ApiResult_FactionIconImageDto.model_rebuild()
ApiResult_FactionRelationDto.model_rebuild()
ApiResult_FactionRelationsDto.model_rebuild()
ApiResult_FogGridDto.model_rebuild()
ApiResult_GameSettingsDto.model_rebuild()
ApiResult_GameStateDto.model_rebuild()
ApiResult_GrowingZoneDto.model_rebuild()
ApiResult_ImageDto.model_rebuild()
ApiResult_IncidentChanceDto.model_rebuild()
ApiResult_IncidentsDto.model_rebuild()
ApiResult_ItemRecipesDto.model_rebuild()
ApiResult_LearningConceptDto.model_rebuild()
ApiResult_List_AnimalDto.model_rebuild()
ApiResult_List_ApiV1PawnDetailedDto.model_rebuild()
ApiResult_List_BillDto.model_rebuild()
ApiResult_List_BuildingDto.model_rebuild()
ApiResult_List_CaravanDto.model_rebuild()
ApiResult_List_FactionsDto.model_rebuild()
ApiResult_List_IncidentWeightDto.model_rebuild()
ApiResult_List_InteractionDefDto.model_rebuild()
ApiResult_List_LearningConceptDto.model_rebuild()
ApiResult_List_LordDto.model_rebuild()
ApiResult_List_MapDto.model_rebuild()
ApiResult_List_ModInfoDto.model_rebuild()
ApiResult_List_OpenWindowDto.model_rebuild()
ApiResult_List_OutfitDto.model_rebuild()
ApiResult_List_PawnDetailedRequestDto.model_rebuild()
ApiResult_List_PawnDto.model_rebuild()
ApiResult_List_PawnOpinionDto.model_rebuild()
ApiResult_List_PawnPositionDto.model_rebuild()
ApiResult_List_RecipeDto.model_rebuild()
ApiResult_List_SettlementDto.model_rebuild()
ApiResult_List_SiteDto.model_rebuild()
ApiResult_List_ThingDto.model_rebuild()
ApiResult_List_TileDto.model_rebuild()
ApiResult_List_TimeAssignmentDto.model_rebuild()
ApiResult_List_TraderKindDto.model_rebuild()
ApiResult_List_UI_AlertDto.model_rebuild()
ApiResult_List_WorkTableDto.model_rebuild()
ApiResult_List_string.model_rebuild()
ApiResult_LordCreateDto.model_rebuild()
ApiResult_MapCreaturesSummaryDto.model_rebuild()
ApiResult_MapFarmSummaryDto.model_rebuild()
ApiResult_MapPowerInfoDto.model_rebuild()
ApiResult_MapRoomsDto.model_rebuild()
ApiResult_MapTerrainDto.model_rebuild()
ApiResult_MapTimeDto.model_rebuild()
ApiResult_MapWeatherDto.model_rebuild()
ApiResult_MapZonesDto.model_rebuild()
ApiResult_Map_OreDataDto.model_rebuild()
ApiResult_MaterialsAtlasList.model_rebuild()
ApiResult_ModInfoDto.model_rebuild()
ApiResult_OpinionAboutPawnDto.model_rebuild()
ApiResult_PawnDetailedDto.model_rebuild()
ApiResult_PawnDetailedRequestDto.model_rebuild()
ApiResult_PawnDto.model_rebuild()
ApiResult_PawnInteractionLogDto.model_rebuild()
ApiResult_PawnInteractionStatusDto.model_rebuild()
ApiResult_PawnInventoryDto.model_rebuild()
ApiResult_PawnRelationsDto.model_rebuild()
ApiResult_PawnSpawnDto.model_rebuild()
ApiResult_QuestsDto.model_rebuild()
ApiResult_ResearchFinishedDto.model_rebuild()
ApiResult_ResearchProjectDto.model_rebuild()
ApiResult_ResearchSummaryDto.model_rebuild()
ApiResult_ResearchTreeDto.model_rebuild()
ApiResult_ResourcesSummaryDto.model_rebuild()
ApiResult_ServerCacheResponseDto.model_rebuild()
ApiResult_StockpileResponseDto.model_rebuild()
ApiResult_StoragesSummaryDto.model_rebuild()
ApiResult_StreamStatusDto.model_rebuild()
ApiResult_ThingSourcesDto.model_rebuild()
ApiResult_TileDetailsDto.model_rebuild()
ApiResult_TileDto.model_rebuild()
ApiResult_TraitDefDto.model_rebuild()
ApiResult_VersionDto.model_rebuild()
ApiResult_WindowCloseResultDto.model_rebuild()
ApiResult_WorkListDto.model_rebuild()
ApiResult_bool.model_rebuild()
ApiResult_string.model_rebuild()
ApiV1PawnDetailedDto.model_rebuild()
BillDto.model_rebuild()
BillRecipeIngredientDto.model_rebuild()
BillReorderRequest.model_rebuild()
BillSuspendRequest.model_rebuild()
BiomeDefDto.model_rebuild()
BlueprintDto.model_rebuild()
BodyDefDto.model_rebuild()
BodyPartDefDto.model_rebuild()
BodyPartsDto.model_rebuild()
BuildingDto.model_rebuild()
CacheStatusDto.model_rebuild()
Camera_CameraScreenshotRequestDto.model_rebuild()
Camera_CameraScreenshotResponseDto.model_rebuild()
Camera_GameContext.model_rebuild()
Camera_ImageData.model_rebuild()
Camera_ImageMetadata.model_rebuild()
Camera_NativeScreenshotRequestDto.model_rebuild()
CaravanDto.model_rebuild()
CaravanPathDto.model_rebuild()
CheckZoneIssueDto.model_rebuild()
CheckZoneIssuesDto.model_rebuild()
CheckZoneRequestDto.model_rebuild()
CheckZoneResultDto.model_rebuild()
ColonistsWorkPrioritiesRequestDto.model_rebuild()
ConfigureModsRequestDto.model_rebuild()
CoordinatesDto.model_rebuild()
CopyAreaRequestDto.model_rebuild()
Core_ApiDocumentation.model_rebuild()
Core_CacheEntryDetail.model_rebuild()
Core_CacheStatistics.model_rebuild()
Core_DocumentationSection.model_rebuild()
Core_DocumentedEndpoint.model_rebuild()
Core_EndpointParameter.model_rebuild()
Core_ParameterValidation.model_rebuild()
Core_RecentHitDetail.model_rebuild()
CreateBillRequest.model_rebuild()
CreateGrowingZoneRequestDto.model_rebuild()
CreateStockpileRequestDto.model_rebuild()
CriticalResourcesDto.model_rebuild()
CropTypeDto.model_rebuild()
DebugConsoleRequest.model_rebuild()
DefsDto.model_rebuild()
DesignateRequestDto.model_rebuild()
DesignationCategoryDefDto.model_rebuild()
DestroyRectRequestDto.model_rebuild()
DialogOptionDto.model_rebuild()
DifficultyDefDto.model_rebuild()
DocumentationHealthDto.model_rebuild()
DrugPolicyDefDto.model_rebuild()
DrugPolicyEntryDto.model_rebuild()
EndpointDto.model_rebuild()
EndpointListDto.model_rebuild()
FactionChangeRelationRequestDto.model_rebuild()
FactionChangeRelationResponceDto.model_rebuild()
FactionDefDto.model_rebuild()
FactionDto.model_rebuild()
FactionIconImageDto.model_rebuild()
FactionRelationDto.model_rebuild()
FactionRelationsDto.model_rebuild()
FactionsDto.model_rebuild()
FloatRange.model_rebuild()
FogGridDto.model_rebuild()
ForceInteractionRequestDto.model_rebuild()
GameConditionDefDto.model_rebuild()
GameLoadRequestDto.model_rebuild()
GameSaveRequestDto.model_rebuild()
GameSettingsDto.model_rebuild()
GameStateDto.model_rebuild()
GeneDefDto.model_rebuild()
GrowingZoneDto.model_rebuild()
HediffDefDto.model_rebuild()
HediffDto.model_rebuild()
HediffStageDto.model_rebuild()
ImageDto.model_rebuild()
ImageUploadRequest.model_rebuild()
IncidentChanceDto.model_rebuild()
IncidentChanceRequestDto.model_rebuild()
IncidentDefDto.model_rebuild()
IncidentDto.model_rebuild()
IncidentParmsDto.model_rebuild()
IncidentWeightDto.model_rebuild()
IncidentsDto.model_rebuild()
Input_AddRelationRequestDto.model_rebuild()
Input_AllDefsRequestDto.model_rebuild()
Input_BillReorderRequest.model_rebuild()
Input_BillSuspendRequest.model_rebuild()
Input_BlueprintDto.model_rebuild()
Input_Camera_CameraScreenshotRequestDto.model_rebuild()
Input_Camera_NativeScreenshotRequestDto.model_rebuild()
Input_CheckZoneRequestDto.model_rebuild()
Input_ColonistsWorkPrioritiesRequestDto.model_rebuild()
Input_ConfigureModsRequestDto.model_rebuild()
Input_CopyAreaRequestDto.model_rebuild()
Input_CreateBillRequest.model_rebuild()
Input_CreateGrowingZoneRequestDto.model_rebuild()
Input_CreateStockpileRequestDto.model_rebuild()
Input_DebugConsoleRequest.model_rebuild()
Input_DesignateRequestDto.model_rebuild()
Input_DestroyRectRequestDto.model_rebuild()
Input_DialogOptionDto.model_rebuild()
Input_FactionChangeRelationRequestDto.model_rebuild()
Input_FloatRange.model_rebuild()
Input_ForceInteractionRequestDto.model_rebuild()
Input_GameLoadRequestDto.model_rebuild()
Input_GameSaveRequestDto.model_rebuild()
Input_ImageUploadRequest.model_rebuild()
Input_IncidentChanceRequestDto.model_rebuild()
Input_IncidentParmsDto.model_rebuild()
Input_IntRangeDto.model_rebuild()
Input_ItemDto.model_rebuild()
Input_LearningConceptMarkDto.model_rebuild()
Input_LordCreateRequestDto.model_rebuild()
Input_MedicalBedRestRequestDto.model_rebuild()
Input_MedicalTendRequestDto.model_rebuild()
Input_NewGameStartRequestDto.model_rebuild()
Input_OverlayRequestDto.model_rebuild()
Input_PasteAreaRequestDto.model_rebuild()
Input_PawnApparelRequest.model_rebuild()
Input_PawnBasicRequest.model_rebuild()
Input_PawnFactionRequest.model_rebuild()
Input_PawnHealthRequest.model_rebuild()
Input_PawnInventoryRequest.model_rebuild()
Input_PawnJobRequestDto.model_rebuild()
Input_PawnNeedsRequest.model_rebuild()
Input_PawnPositionRequest.model_rebuild()
Input_PawnSkillsRequest.model_rebuild()
Input_PawnSpawnRequestDto.model_rebuild()
Input_PawnStatusRequest.model_rebuild()
Input_PawnTimeAssignmentRequestDto.model_rebuild()
Input_PawnTraitsRequest.model_rebuild()
Input_PositionDto.model_rebuild()
Input_RepairPositionsRequestDto.model_rebuild()
Input_RepairRectRequestDto.model_rebuild()
Input_SavedBuildingDto.model_rebuild()
Input_SavedTerrainDto.model_rebuild()
Input_SelectAreaRequestDto.model_rebuild()
Input_SendLetterRequestDto.model_rebuild()
Input_SetForbiddenRequestDto.model_rebuild()
Input_SkillEntryDto.model_rebuild()
Input_SpawnDropPodRequestDto.model_rebuild()
Input_SpawnItemRequestDto.model_rebuild()
Input_StreamConfigDto.model_rebuild()
Input_StuffColorRequest.model_rebuild()
Input_ThingDto.model_rebuild()
Input_ThingsAtCellRequestDto.model_rebuild()
Input_TraitEntryDto.model_rebuild()
Input_TriggerIncidentRequestDto.model_rebuild()
Input_UpdateBillRequest.model_rebuild()
Input_UpdateStockpileRequestDto.model_rebuild()
Input_WindowCloseRequestDto.model_rebuild()
Input_WindowDialogRequestDto.model_rebuild()
Input_WindowMessageRequestDto.model_rebuild()
Input_WorkPriorityRequestDto.model_rebuild()
IntRangeDto.model_rebuild()
InteractionDefDto.model_rebuild()
InteractionLogEntryDto.model_rebuild()
ItemDto.model_rebuild()
ItemRecipesDto.model_rebuild()
JobDefDto.model_rebuild()
JoyKindDefDto.model_rebuild()
LearningConceptDto.model_rebuild()
LearningConceptMarkDto.model_rebuild()
LifeStageAgeDto.model_rebuild()
LordCreateDto.model_rebuild()
LordCreateRequestDto.model_rebuild()
LordDto.model_rebuild()
MapCreaturesSummaryDto.model_rebuild()
MapDto.model_rebuild()
MapFarmSummaryDto.model_rebuild()
MapPowerInfoDto.model_rebuild()
MapRoomsDto.model_rebuild()
MapTerrainDto.model_rebuild()
MapTimeDto.model_rebuild()
MapWeatherDto.model_rebuild()
MapZonesDto.model_rebuild()
Map_OreDataDto.model_rebuild()
Map_OreGroupDto.model_rebuild()
MaterialsAtlasList.model_rebuild()
MedicalBedRestRequestDto.model_rebuild()
MedicalInfoDto.model_rebuild()
MedicalTendRequestDto.model_rebuild()
MemeDefDto.model_rebuild()
MentalStateDefDto.model_rebuild()
ModInfoDto.model_rebuild()
NeedDefDto.model_rebuild()
NewGameStartRequestDto.model_rebuild()
OpenWindowDto.model_rebuild()
OpinionAboutPawnDto.model_rebuild()
OpinionBreakdownDto.model_rebuild()
OutfitDto.model_rebuild()
OverlayRequestDto.model_rebuild()
PasteAreaRequestDto.model_rebuild()
PawnApparelRequest.model_rebuild()
PawnBasicRequest.model_rebuild()
PawnDetailedDto.model_rebuild()
PawnDetailedRequestDto.model_rebuild()
PawnDto.model_rebuild()
PawnFactionRequest.model_rebuild()
PawnHealthRequest.model_rebuild()
PawnInteractionLogDto.model_rebuild()
PawnInteractionStatusDto.model_rebuild()
PawnInventoryDto.model_rebuild()
PawnInventoryRequest.model_rebuild()
PawnJobRequestDto.model_rebuild()
PawnKindDefDto.model_rebuild()
PawnNeedsRequest.model_rebuild()
PawnOpinionDto.model_rebuild()
PawnPositionDto.model_rebuild()
PawnPositionRequest.model_rebuild()
PawnRelationEntryDto.model_rebuild()
PawnRelationsDto.model_rebuild()
PawnSkillsRequest.model_rebuild()
PawnSpawnDto.model_rebuild()
PawnSpawnRequestDto.model_rebuild()
PawnStatusRequest.model_rebuild()
PawnTimeAssignmentRequestDto.model_rebuild()
PawnTraitsRequest.model_rebuild()
PlantDefDto.model_rebuild()
PoliciesInfoDto.model_rebuild()
PositionDto.model_rebuild()
PreceptDefDto.model_rebuild()
QuestDto.model_rebuild()
QuestsDto.model_rebuild()
RecipeDefDto.model_rebuild()
RecipeDto.model_rebuild()
RecipeIngredientDto.model_rebuild()
RecipeProducerDto.model_rebuild()
RecipeProductDto.model_rebuild()
RecipeSkillDto.model_rebuild()
RelationDto.model_rebuild()
RepairPositionsRequestDto.model_rebuild()
RepairRectRequestDto.model_rebuild()
ResearchCategoryDto.model_rebuild()
ResearchFinishedDto.model_rebuild()
ResearchProjectDefDto.model_rebuild()
ResearchProjectDto.model_rebuild()
ResearchSummaryDto.model_rebuild()
ResearchTreeDto.model_rebuild()
ResourceCategoryDto.model_rebuild()
ResourcesFoodSummaryDto.model_rebuild()
ResourcesSummaryDto.model_rebuild()
RoomDto.model_rebuild()
RoomRoleDefDto.model_rebuild()
RoomStatDefDto.model_rebuild()
RoomStatScoreStageDto.model_rebuild()
RotStatusInfoDto.model_rebuild()
RottingFoodItemDto.model_rebuild()
SavedBuildingDto.model_rebuild()
SavedTerrainDto.model_rebuild()
SelectAreaRequestDto.model_rebuild()
SendLetterRequestDto.model_rebuild()
ServerCacheResponseDto.model_rebuild()
SetForbiddenRequestDto.model_rebuild()
SettlementDto.model_rebuild()
SiteDto.model_rebuild()
SkillDefDto.model_rebuild()
SkillDto.model_rebuild()
SkillEntryDto.model_rebuild()
SkillRequirementDto.model_rebuild()
SocialInfoDto.model_rebuild()
SoundDefDto.model_rebuild()
SpawnDropPodRequestDto.model_rebuild()
SpawnItemRequestDto.model_rebuild()
StatDefDto.model_rebuild()
StatModifierDto.model_rebuild()
StockRuleDto.model_rebuild()
StockpileResponseDto.model_rebuild()
StoragesSummaryDto.model_rebuild()
StorytellerDefDto.model_rebuild()
StreamConfigDto.model_rebuild()
StreamStatusDto.model_rebuild()
StuffColorRequest.model_rebuild()
TerrainDefDto.model_rebuild()
ThingCostDto.model_rebuild()
ThingDefDto.model_rebuild()
ThingDto.model_rebuild()
ThingFilterDto.model_rebuild()
ThingRecipeDto.model_rebuild()
ThingSourcesDto.model_rebuild()
ThingsAtCellRequestDto.model_rebuild()
ThoughtDefDto.model_rebuild()
ThoughtStageDto.model_rebuild()
TileDetailsDto.model_rebuild()
TileDto.model_rebuild()
TimeAssignmentDto.model_rebuild()
TraderKindDto.model_rebuild()
TraitDefDto.model_rebuild()
TraitDegreeDto.model_rebuild()
TraitDto.model_rebuild()
TraitEntryDto.model_rebuild()
TriggerIncidentRequestDto.model_rebuild()
UI_AlertDto.model_rebuild()
UpdateBillRequest.model_rebuild()
UpdateStockpileRequestDto.model_rebuild()
VersionDto.model_rebuild()
WeatherDefDto.model_rebuild()
WindowCloseRequestDto.model_rebuild()
WindowCloseResultDto.model_rebuild()
WindowDialogRequestDto.model_rebuild()
WindowMessageRequestDto.model_rebuild()
WorkInfoDto.model_rebuild()
WorkListDto.model_rebuild()
WorkPriorityDto.model_rebuild()
WorkPriorityRequestDto.model_rebuild()
WorkTableDto.model_rebuild()
WorkTypeDefDto.model_rebuild()
WorldObjectDefDto.model_rebuild()
ZoneDto.model_rebuild()
WorkSettings.model_rebuild()
ResourceCell.model_rebuild()
ResourceLocation.model_rebuild()
TerrainResource.model_rebuild()
PlantResource.model_rebuild()
AnimalResource.model_rebuild()
MineralResource.model_rebuild()
SupplyResource.model_rebuild()
CropResource.model_rebuild()
FishingResource.model_rebuild()
MapResourceOverview.model_rebuild()
ConstructionWorkerObservation.model_rebuild()
ConstructionWorkerCheck.model_rebuild()
ConstructionMaterialObservation.model_rebuild()
ConstructionWorkSite.model_rebuild()
ConstructionWorkOverview.model_rebuild()
Construction_DefinitionQuery.model_rebuild()
Construction_Cost.model_rebuild()
Construction_Material.model_rebuild()
Construction_BuildingDefinition.model_rebuild()
Construction_PageBuildingDefinition.model_rebuild()
Construction_RoomQuery.model_rebuild()
Construction_PositionDto.model_rebuild()
Construction_RoomDto.model_rebuild()
Construction_PageRoomDto.model_rebuild()
Construction_Cell.model_rebuild()
Construction_Placement.model_rebuild()
Construction_ConstructionRequest.model_rebuild()
Construction_PlacementResult.model_rebuild()
Construction_ConstructionResult.model_rebuild()
Construction_ContractError.model_rebuild()
Construction_MapQuery.model_rebuild()
Construction_ConstructionThing.model_rebuild()
Construction_ConstructionState.model_rebuild()
Construction_AreaQuery.model_rebuild()
Construction_AreaCell.model_rebuild()
Construction_AreaResult.model_rebuild()
Construction_AllowAllRequest.model_rebuild()
Construction_AllowAllResult.model_rebuild()
get_api_openapi_json_Query.model_rebuild()
post_v1_builder_blueprint_Query.model_rebuild()
post_v1_builder_check_zone_Query.model_rebuild()
post_v1_builder_copy_Query.model_rebuild()
post_v1_builder_paste_Query.model_rebuild()
get_v1_buildings_bill_Query.model_rebuild()
delete_v1_buildings_bill_remove_Query.model_rebuild()
put_v1_buildings_bill_reorder_Query.model_rebuild()
put_v1_buildings_bill_suspend_Query.model_rebuild()
put_v1_buildings_bill_update_Query.model_rebuild()
get_v1_buildings_bills_Query.model_rebuild()
post_v1_buildings_bills_add_Query.model_rebuild()
delete_v1_buildings_bills_remove_Query.model_rebuild()
get_v1_buildings_recipes_Query.model_rebuild()
post_v1_cache_clear_Query.model_rebuild()
post_v1_cache_disable_Query.model_rebuild()
post_v1_cache_enable_Query.model_rebuild()
get_v1_cache_status_Query.model_rebuild()
post_v1_camera_change_position_Query.model_rebuild()
post_v1_camera_change_zoom_Query.model_rebuild()
post_v1_camera_follow_pawn_Query.model_rebuild()
post_v1_camera_screenshot_Query.model_rebuild()
post_v1_camera_screenshot_native_Query.model_rebuild()
post_v1_camera_stream_setup_Query.model_rebuild()
post_v1_camera_stream_start_Query.model_rebuild()
get_v1_camera_stream_status_Query.model_rebuild()
post_v1_camera_stream_stop_Query.model_rebuild()
get_v1_client_learning_active_Query.model_rebuild()
get_v1_client_learning_all_Query.model_rebuild()
get_v1_client_learning_concept_Query.model_rebuild()
get_v1_client_learning_defs_Query.model_rebuild()
post_v1_client_learning_mark_learned_Query.model_rebuild()
get_v1_colonist_Query.model_rebuild()
get_v1_colonist_body_image_Query.model_rebuild()
get_v1_colonist_detailed_Query.model_rebuild()
get_v1_colonist_inventory_Query.model_rebuild()
get_v1_colonist_opinion_about_Query.model_rebuild()
post_v1_colonist_time_assignment_Query.model_rebuild()
post_v1_colonist_work_priority_Query.model_rebuild()
get_v1_colonists_Query.model_rebuild()
get_v1_colonists_detailed_Query.model_rebuild()
get_v1_colonists_positions_Query.model_rebuild()
post_v1_colonists_work_priority_Query.model_rebuild()
get_v1_core_docs_export_Query.model_rebuild()
get_v1_datetime_Query.model_rebuild()
get_v1_datetime_tile_Query.model_rebuild()
get_v1_def_all_Query.model_rebuild()
post_v1_deselect_Query.model_rebuild()
post_v1_dev_console_Query.model_rebuild()
get_v1_dev_endpoints_Query.model_rebuild()
get_v1_dev_materials_atlas_Query.model_rebuild()
post_v1_dev_materials_atlas_clear_Query.model_rebuild()
post_v1_dev_stuff_color_Query.model_rebuild()
get_v1_docs_Query.model_rebuild()
get_v1_docs_health_Query.model_rebuild()
get_v1_events_Query.model_rebuild()
get_v1_faction_Query.model_rebuild()
post_v1_faction_change_goodwill_Query.model_rebuild()
get_v1_faction_def_Query.model_rebuild()
post_v1_faction_goodwill_Query.model_rebuild()
get_v1_faction_icon_Query.model_rebuild()
get_v1_faction_player_Query.model_rebuild()
get_v1_faction_relation_with_Query.model_rebuild()
get_v1_faction_relations_Query.model_rebuild()
get_v1_factions_Query.model_rebuild()
get_v1_game_defs_interactions_Query.model_rebuild()
post_v1_game_load_Query.model_rebuild()
post_v1_game_main_menu_Query.model_rebuild()
post_v1_game_quit_Query.model_rebuild()
post_v1_game_save_Query.model_rebuild()
post_v1_game_select_area_Query.model_rebuild()
post_v1_game_send_letter_Query.model_rebuild()
get_v1_game_settings_Query.model_rebuild()
get_v1_game_settings_run_in_background_Query.model_rebuild()
post_v1_game_settings_toggle_run_in_background_Query.model_rebuild()
post_v1_game_speed_Query.model_rebuild()
post_v1_game_start_Query.model_rebuild()
post_v1_game_start_devquick_Query.model_rebuild()
get_v1_game_state_Query.model_rebuild()
get_v1_incident_chance_Query.model_rebuild()
post_v1_incident_trigger_Query.model_rebuild()
get_v1_incidents_Query.model_rebuild()
get_v1_incidents_top_Query.model_rebuild()
post_v1_item_change_image_Query.model_rebuild()
get_v1_item_image_Query.model_rebuild()
get_v1_item_recipes_Query.model_rebuild()
get_v1_item_sources_Query.model_rebuild()
post_v1_item_spawn_Query.model_rebuild()
post_v1_jobs_make_equip_Query.model_rebuild()
get_v1_lords_Query.model_rebuild()
post_v1_lords_create_Query.model_rebuild()
get_v1_map_animals_Query.model_rebuild()
get_v1_map_building_info_Query.model_rebuild()
post_v1_map_building_power_Query.model_rebuild()
get_v1_map_buildings_Query.model_rebuild()
get_v1_map_creatures_summary_Query.model_rebuild()
post_v1_map_destroy_corpses_Query.model_rebuild()
post_v1_map_destroy_forbidden_Query.model_rebuild()
post_v1_map_destroy_rect_Query.model_rebuild()
post_v1_map_droppod_Query.model_rebuild()
get_v1_map_farm_summary_Query.model_rebuild()
get_v1_map_fog_grid_Query.model_rebuild()
get_v1_map_ore_Query.model_rebuild()
get_v1_map_pawns_Query.model_rebuild()
get_v1_map_plants_Query.model_rebuild()
get_v1_map_power_info_Query.model_rebuild()
post_v1_map_repair_positions_Query.model_rebuild()
post_v1_map_repair_rect_Query.model_rebuild()
get_v1_map_rooms_Query.model_rebuild()
get_v1_map_terrain_Query.model_rebuild()
get_v1_map_things_Query.model_rebuild()
get_v1_map_things_at_Query.model_rebuild()
get_v1_map_things_radius_Query.model_rebuild()
get_v1_map_weather_Query.model_rebuild()
post_v1_map_weather_change_Query.model_rebuild()
get_v1_map_work_tables_Query.model_rebuild()
get_v1_map_zone_growing_Query.model_rebuild()
post_v1_map_zone_growing_Query.model_rebuild()
post_v1_map_zone_stockpile_Query.model_rebuild()
delete_v1_map_zone_stockpile_delete_Query.model_rebuild()
post_v1_map_zone_stockpile_update_Query.model_rebuild()
get_v1_map_zones_Query.model_rebuild()
get_v1_maps_Query.model_rebuild()
post_v1_mods_configure_Query.model_rebuild()
get_v1_mods_info_Query.model_rebuild()
get_v1_mods_list_Query.model_rebuild()
get_v1_mods_preview_Query.model_rebuild()
post_v1_open_tab_Query.model_rebuild()
post_v1_order_designate_area_Query.model_rebuild()
get_v1_outfits_Query.model_rebuild()
post_v1_pawn_edit_apparel_Query.model_rebuild()
post_v1_pawn_edit_basic_Query.model_rebuild()
post_v1_pawn_edit_faction_Query.model_rebuild()
post_v1_pawn_edit_health_Query.model_rebuild()
post_v1_pawn_edit_inventory_Query.model_rebuild()
post_v1_pawn_edit_needs_Query.model_rebuild()
post_v1_pawn_edit_position_Query.model_rebuild()
post_v1_pawn_edit_skills_Query.model_rebuild()
post_v1_pawn_edit_status_Query.model_rebuild()
post_v1_pawn_edit_traits_Query.model_rebuild()
post_v1_pawn_job_Query.model_rebuild()
post_v1_pawn_medical_bed_rest_Query.model_rebuild()
post_v1_pawn_medical_tend_Query.model_rebuild()
get_v1_pawn_portrait_image_Query.model_rebuild()
post_v1_pawn_spawn_Query.model_rebuild()
get_v1_pawns_details_Query.model_rebuild()
get_v1_pawns_interactions_Query.model_rebuild()
post_v1_pawns_interactions_force_Query.model_rebuild()
get_v1_pawns_interactions_log_Query.model_rebuild()
get_v1_pawns_inventory_Query.model_rebuild()
get_v1_pawns_opinions_Query.model_rebuild()
get_v1_pawns_relations_Query.model_rebuild()
post_v1_pawns_relations_add_Query.model_rebuild()
delete_v1_pawns_relations_remove_Query.model_rebuild()
get_v1_quests_Query.model_rebuild()
get_v1_research_finished_Query.model_rebuild()
get_v1_research_progress_Query.model_rebuild()
get_v1_research_project_Query.model_rebuild()
post_v1_research_stop_Query.model_rebuild()
get_v1_research_summary_Query.model_rebuild()
post_v1_research_target_Query.model_rebuild()
get_v1_research_tree_Query.model_rebuild()
get_v1_resources_storages_summary_Query.model_rebuild()
get_v1_resources_stored_Query.model_rebuild()
get_v1_resources_summary_Query.model_rebuild()
post_v1_select_Query.model_rebuild()
get_v1_terrain_image_Query.model_rebuild()
post_v1_things_set_forbidden_Query.model_rebuild()
get_v1_time_assignments_Query.model_rebuild()
get_v1_traders_defs_Query.model_rebuild()
get_v1_trait_def_Query.model_rebuild()
get_v1_ui_alerts_Query.model_rebuild()
post_v1_ui_announce_Query.model_rebuild()
post_v1_ui_dialog_Query.model_rebuild()
post_v1_ui_message_Query.model_rebuild()
post_v1_ui_window_close_Query.model_rebuild()
get_v1_ui_windows_Query.model_rebuild()
get_v1_version_Query.model_rebuild()
get_v1_work_list_Query.model_rebuild()
get_v1_world_caravan_path_Query.model_rebuild()
get_v1_world_caravans_Query.model_rebuild()
get_v1_world_grid_Query.model_rebuild()
get_v1_world_grid_area_Query.model_rebuild()
get_v1_world_player_settlements_Query.model_rebuild()
get_v1_world_settlements_Query.model_rebuild()
get_v1_world_sites_Query.model_rebuild()
get_v1_world_tile_Query.model_rebuild()
get_v1_world_tile_coordinates_Query.model_rebuild()
get_v1_world_tile_details_Query.model_rebuild()
get_v2_colonist_detailed_Query.model_rebuild()
get_v2_colonists_detailed_Query.model_rebuild()
get_api_v2_construction_contracts_Query.model_rebuild()
construction_definitions_Query.model_rebuild()
construction_inspect_Query.model_rebuild()
get_api_v2_construction_openapi_Query.model_rebuild()
construction_place_Query.model_rebuild()
construction_rooms_Query.model_rebuild()
construction_state_Query.model_rebuild()
construction_area_Query.model_rebuild()
get_v1_work_settings_Query.model_rebuild()
post_v1_work_settings_Query.model_rebuild()
get_v1_map_resource_overview_Query.model_rebuild()
get_v1_map_construction_work_Query.model_rebuild()
orders_unforbid_all_Query.model_rebuild()
orders_forbidden_overview_Query.model_rebuild()

QUERY_TYPES = {
    'get_api_openapi_json': TypeAdapter(get_api_openapi_json_Query),
    'post_v1_builder_blueprint': TypeAdapter(post_v1_builder_blueprint_Query),
    'post_v1_builder_check_zone': TypeAdapter(post_v1_builder_check_zone_Query),
    'post_v1_builder_copy': TypeAdapter(post_v1_builder_copy_Query),
    'post_v1_builder_paste': TypeAdapter(post_v1_builder_paste_Query),
    'get_v1_buildings_bill': TypeAdapter(get_v1_buildings_bill_Query),
    'delete_v1_buildings_bill_remove': TypeAdapter(delete_v1_buildings_bill_remove_Query),
    'put_v1_buildings_bill_reorder': TypeAdapter(put_v1_buildings_bill_reorder_Query),
    'put_v1_buildings_bill_suspend': TypeAdapter(put_v1_buildings_bill_suspend_Query),
    'put_v1_buildings_bill_update': TypeAdapter(put_v1_buildings_bill_update_Query),
    'get_v1_buildings_bills': TypeAdapter(get_v1_buildings_bills_Query),
    'post_v1_buildings_bills_add': TypeAdapter(post_v1_buildings_bills_add_Query),
    'delete_v1_buildings_bills_remove': TypeAdapter(delete_v1_buildings_bills_remove_Query),
    'get_v1_buildings_recipes': TypeAdapter(get_v1_buildings_recipes_Query),
    'post_v1_cache_clear': TypeAdapter(post_v1_cache_clear_Query),
    'post_v1_cache_disable': TypeAdapter(post_v1_cache_disable_Query),
    'post_v1_cache_enable': TypeAdapter(post_v1_cache_enable_Query),
    'get_v1_cache_status': TypeAdapter(get_v1_cache_status_Query),
    'post_v1_camera_change_position': TypeAdapter(post_v1_camera_change_position_Query),
    'post_v1_camera_change_zoom': TypeAdapter(post_v1_camera_change_zoom_Query),
    'post_v1_camera_follow_pawn': TypeAdapter(post_v1_camera_follow_pawn_Query),
    'post_v1_camera_screenshot': TypeAdapter(post_v1_camera_screenshot_Query),
    'post_v1_camera_screenshot_native': TypeAdapter(post_v1_camera_screenshot_native_Query),
    'post_v1_camera_stream_setup': TypeAdapter(post_v1_camera_stream_setup_Query),
    'post_v1_camera_stream_start': TypeAdapter(post_v1_camera_stream_start_Query),
    'get_v1_camera_stream_status': TypeAdapter(get_v1_camera_stream_status_Query),
    'post_v1_camera_stream_stop': TypeAdapter(post_v1_camera_stream_stop_Query),
    'get_v1_client_learning_active': TypeAdapter(get_v1_client_learning_active_Query),
    'get_v1_client_learning_all': TypeAdapter(get_v1_client_learning_all_Query),
    'get_v1_client_learning_concept': TypeAdapter(get_v1_client_learning_concept_Query),
    'get_v1_client_learning_defs': TypeAdapter(get_v1_client_learning_defs_Query),
    'post_v1_client_learning_mark_learned': TypeAdapter(post_v1_client_learning_mark_learned_Query),
    'get_v1_colonist': TypeAdapter(get_v1_colonist_Query),
    'get_v1_colonist_body_image': TypeAdapter(get_v1_colonist_body_image_Query),
    'get_v1_colonist_detailed': TypeAdapter(get_v1_colonist_detailed_Query),
    'get_v1_colonist_inventory': TypeAdapter(get_v1_colonist_inventory_Query),
    'get_v1_colonist_opinion_about': TypeAdapter(get_v1_colonist_opinion_about_Query),
    'post_v1_colonist_time_assignment': TypeAdapter(post_v1_colonist_time_assignment_Query),
    'post_v1_colonist_work_priority': TypeAdapter(post_v1_colonist_work_priority_Query),
    'get_v1_colonists': TypeAdapter(get_v1_colonists_Query),
    'get_v1_colonists_detailed': TypeAdapter(get_v1_colonists_detailed_Query),
    'get_v1_colonists_positions': TypeAdapter(get_v1_colonists_positions_Query),
    'post_v1_colonists_work_priority': TypeAdapter(post_v1_colonists_work_priority_Query),
    'get_v1_core_docs_export': TypeAdapter(get_v1_core_docs_export_Query),
    'get_v1_datetime': TypeAdapter(get_v1_datetime_Query),
    'get_v1_datetime_tile': TypeAdapter(get_v1_datetime_tile_Query),
    'get_v1_def_all': TypeAdapter(get_v1_def_all_Query),
    'post_v1_deselect': TypeAdapter(post_v1_deselect_Query),
    'post_v1_dev_console': TypeAdapter(post_v1_dev_console_Query),
    'get_v1_dev_endpoints': TypeAdapter(get_v1_dev_endpoints_Query),
    'get_v1_dev_materials_atlas': TypeAdapter(get_v1_dev_materials_atlas_Query),
    'post_v1_dev_materials_atlas_clear': TypeAdapter(post_v1_dev_materials_atlas_clear_Query),
    'post_v1_dev_stuff_color': TypeAdapter(post_v1_dev_stuff_color_Query),
    'get_v1_docs': TypeAdapter(get_v1_docs_Query),
    'get_v1_docs_health': TypeAdapter(get_v1_docs_health_Query),
    'get_v1_events': TypeAdapter(get_v1_events_Query),
    'get_v1_faction': TypeAdapter(get_v1_faction_Query),
    'post_v1_faction_change_goodwill': TypeAdapter(post_v1_faction_change_goodwill_Query),
    'get_v1_faction_def': TypeAdapter(get_v1_faction_def_Query),
    'post_v1_faction_goodwill': TypeAdapter(post_v1_faction_goodwill_Query),
    'get_v1_faction_icon': TypeAdapter(get_v1_faction_icon_Query),
    'get_v1_faction_player': TypeAdapter(get_v1_faction_player_Query),
    'get_v1_faction_relation_with': TypeAdapter(get_v1_faction_relation_with_Query),
    'get_v1_faction_relations': TypeAdapter(get_v1_faction_relations_Query),
    'get_v1_factions': TypeAdapter(get_v1_factions_Query),
    'get_v1_game_defs_interactions': TypeAdapter(get_v1_game_defs_interactions_Query),
    'post_v1_game_load': TypeAdapter(post_v1_game_load_Query),
    'post_v1_game_main_menu': TypeAdapter(post_v1_game_main_menu_Query),
    'post_v1_game_quit': TypeAdapter(post_v1_game_quit_Query),
    'post_v1_game_save': TypeAdapter(post_v1_game_save_Query),
    'post_v1_game_select_area': TypeAdapter(post_v1_game_select_area_Query),
    'post_v1_game_send_letter': TypeAdapter(post_v1_game_send_letter_Query),
    'get_v1_game_settings': TypeAdapter(get_v1_game_settings_Query),
    'get_v1_game_settings_run_in_background': TypeAdapter(get_v1_game_settings_run_in_background_Query),
    'post_v1_game_settings_toggle_run_in_background': TypeAdapter(post_v1_game_settings_toggle_run_in_background_Query),
    'post_v1_game_speed': TypeAdapter(post_v1_game_speed_Query),
    'post_v1_game_start': TypeAdapter(post_v1_game_start_Query),
    'post_v1_game_start_devquick': TypeAdapter(post_v1_game_start_devquick_Query),
    'get_v1_game_state': TypeAdapter(get_v1_game_state_Query),
    'get_v1_incident_chance': TypeAdapter(get_v1_incident_chance_Query),
    'post_v1_incident_trigger': TypeAdapter(post_v1_incident_trigger_Query),
    'get_v1_incidents': TypeAdapter(get_v1_incidents_Query),
    'get_v1_incidents_top': TypeAdapter(get_v1_incidents_top_Query),
    'post_v1_item_change_image': TypeAdapter(post_v1_item_change_image_Query),
    'get_v1_item_image': TypeAdapter(get_v1_item_image_Query),
    'get_v1_item_recipes': TypeAdapter(get_v1_item_recipes_Query),
    'get_v1_item_sources': TypeAdapter(get_v1_item_sources_Query),
    'post_v1_item_spawn': TypeAdapter(post_v1_item_spawn_Query),
    'post_v1_jobs_make_equip': TypeAdapter(post_v1_jobs_make_equip_Query),
    'get_v1_lords': TypeAdapter(get_v1_lords_Query),
    'post_v1_lords_create': TypeAdapter(post_v1_lords_create_Query),
    'get_v1_map_animals': TypeAdapter(get_v1_map_animals_Query),
    'get_v1_map_building_info': TypeAdapter(get_v1_map_building_info_Query),
    'post_v1_map_building_power': TypeAdapter(post_v1_map_building_power_Query),
    'get_v1_map_buildings': TypeAdapter(get_v1_map_buildings_Query),
    'get_v1_map_creatures_summary': TypeAdapter(get_v1_map_creatures_summary_Query),
    'post_v1_map_destroy_corpses': TypeAdapter(post_v1_map_destroy_corpses_Query),
    'post_v1_map_destroy_forbidden': TypeAdapter(post_v1_map_destroy_forbidden_Query),
    'post_v1_map_destroy_rect': TypeAdapter(post_v1_map_destroy_rect_Query),
    'post_v1_map_droppod': TypeAdapter(post_v1_map_droppod_Query),
    'get_v1_map_farm_summary': TypeAdapter(get_v1_map_farm_summary_Query),
    'get_v1_map_fog_grid': TypeAdapter(get_v1_map_fog_grid_Query),
    'get_v1_map_ore': TypeAdapter(get_v1_map_ore_Query),
    'get_v1_map_pawns': TypeAdapter(get_v1_map_pawns_Query),
    'get_v1_map_plants': TypeAdapter(get_v1_map_plants_Query),
    'get_v1_map_power_info': TypeAdapter(get_v1_map_power_info_Query),
    'post_v1_map_repair_positions': TypeAdapter(post_v1_map_repair_positions_Query),
    'post_v1_map_repair_rect': TypeAdapter(post_v1_map_repair_rect_Query),
    'get_v1_map_rooms': TypeAdapter(get_v1_map_rooms_Query),
    'get_v1_map_terrain': TypeAdapter(get_v1_map_terrain_Query),
    'get_v1_map_things': TypeAdapter(get_v1_map_things_Query),
    'get_v1_map_things_at': TypeAdapter(get_v1_map_things_at_Query),
    'get_v1_map_things_radius': TypeAdapter(get_v1_map_things_radius_Query),
    'get_v1_map_weather': TypeAdapter(get_v1_map_weather_Query),
    'post_v1_map_weather_change': TypeAdapter(post_v1_map_weather_change_Query),
    'get_v1_map_work_tables': TypeAdapter(get_v1_map_work_tables_Query),
    'get_v1_map_zone_growing': TypeAdapter(get_v1_map_zone_growing_Query),
    'post_v1_map_zone_growing': TypeAdapter(post_v1_map_zone_growing_Query),
    'post_v1_map_zone_stockpile': TypeAdapter(post_v1_map_zone_stockpile_Query),
    'delete_v1_map_zone_stockpile_delete': TypeAdapter(delete_v1_map_zone_stockpile_delete_Query),
    'post_v1_map_zone_stockpile_update': TypeAdapter(post_v1_map_zone_stockpile_update_Query),
    'get_v1_map_zones': TypeAdapter(get_v1_map_zones_Query),
    'get_v1_maps': TypeAdapter(get_v1_maps_Query),
    'post_v1_mods_configure': TypeAdapter(post_v1_mods_configure_Query),
    'get_v1_mods_info': TypeAdapter(get_v1_mods_info_Query),
    'get_v1_mods_list': TypeAdapter(get_v1_mods_list_Query),
    'get_v1_mods_preview': TypeAdapter(get_v1_mods_preview_Query),
    'post_v1_open_tab': TypeAdapter(post_v1_open_tab_Query),
    'post_v1_order_designate_area': TypeAdapter(post_v1_order_designate_area_Query),
    'get_v1_outfits': TypeAdapter(get_v1_outfits_Query),
    'post_v1_pawn_edit_apparel': TypeAdapter(post_v1_pawn_edit_apparel_Query),
    'post_v1_pawn_edit_basic': TypeAdapter(post_v1_pawn_edit_basic_Query),
    'post_v1_pawn_edit_faction': TypeAdapter(post_v1_pawn_edit_faction_Query),
    'post_v1_pawn_edit_health': TypeAdapter(post_v1_pawn_edit_health_Query),
    'post_v1_pawn_edit_inventory': TypeAdapter(post_v1_pawn_edit_inventory_Query),
    'post_v1_pawn_edit_needs': TypeAdapter(post_v1_pawn_edit_needs_Query),
    'post_v1_pawn_edit_position': TypeAdapter(post_v1_pawn_edit_position_Query),
    'post_v1_pawn_edit_skills': TypeAdapter(post_v1_pawn_edit_skills_Query),
    'post_v1_pawn_edit_status': TypeAdapter(post_v1_pawn_edit_status_Query),
    'post_v1_pawn_edit_traits': TypeAdapter(post_v1_pawn_edit_traits_Query),
    'post_v1_pawn_job': TypeAdapter(post_v1_pawn_job_Query),
    'post_v1_pawn_medical_bed_rest': TypeAdapter(post_v1_pawn_medical_bed_rest_Query),
    'post_v1_pawn_medical_tend': TypeAdapter(post_v1_pawn_medical_tend_Query),
    'get_v1_pawn_portrait_image': TypeAdapter(get_v1_pawn_portrait_image_Query),
    'post_v1_pawn_spawn': TypeAdapter(post_v1_pawn_spawn_Query),
    'get_v1_pawns_details': TypeAdapter(get_v1_pawns_details_Query),
    'get_v1_pawns_interactions': TypeAdapter(get_v1_pawns_interactions_Query),
    'post_v1_pawns_interactions_force': TypeAdapter(post_v1_pawns_interactions_force_Query),
    'get_v1_pawns_interactions_log': TypeAdapter(get_v1_pawns_interactions_log_Query),
    'get_v1_pawns_inventory': TypeAdapter(get_v1_pawns_inventory_Query),
    'get_v1_pawns_opinions': TypeAdapter(get_v1_pawns_opinions_Query),
    'get_v1_pawns_relations': TypeAdapter(get_v1_pawns_relations_Query),
    'post_v1_pawns_relations_add': TypeAdapter(post_v1_pawns_relations_add_Query),
    'delete_v1_pawns_relations_remove': TypeAdapter(delete_v1_pawns_relations_remove_Query),
    'get_v1_quests': TypeAdapter(get_v1_quests_Query),
    'get_v1_research_finished': TypeAdapter(get_v1_research_finished_Query),
    'get_v1_research_progress': TypeAdapter(get_v1_research_progress_Query),
    'get_v1_research_project': TypeAdapter(get_v1_research_project_Query),
    'post_v1_research_stop': TypeAdapter(post_v1_research_stop_Query),
    'get_v1_research_summary': TypeAdapter(get_v1_research_summary_Query),
    'post_v1_research_target': TypeAdapter(post_v1_research_target_Query),
    'get_v1_research_tree': TypeAdapter(get_v1_research_tree_Query),
    'get_v1_resources_storages_summary': TypeAdapter(get_v1_resources_storages_summary_Query),
    'get_v1_resources_stored': TypeAdapter(get_v1_resources_stored_Query),
    'get_v1_resources_summary': TypeAdapter(get_v1_resources_summary_Query),
    'post_v1_select': TypeAdapter(post_v1_select_Query),
    'get_v1_terrain_image': TypeAdapter(get_v1_terrain_image_Query),
    'post_v1_things_set_forbidden': TypeAdapter(post_v1_things_set_forbidden_Query),
    'get_v1_time_assignments': TypeAdapter(get_v1_time_assignments_Query),
    'get_v1_traders_defs': TypeAdapter(get_v1_traders_defs_Query),
    'get_v1_trait_def': TypeAdapter(get_v1_trait_def_Query),
    'get_v1_ui_alerts': TypeAdapter(get_v1_ui_alerts_Query),
    'post_v1_ui_announce': TypeAdapter(post_v1_ui_announce_Query),
    'post_v1_ui_dialog': TypeAdapter(post_v1_ui_dialog_Query),
    'post_v1_ui_message': TypeAdapter(post_v1_ui_message_Query),
    'post_v1_ui_window_close': TypeAdapter(post_v1_ui_window_close_Query),
    'get_v1_ui_windows': TypeAdapter(get_v1_ui_windows_Query),
    'get_v1_version': TypeAdapter(get_v1_version_Query),
    'get_v1_work_list': TypeAdapter(get_v1_work_list_Query),
    'get_v1_world_caravan_path': TypeAdapter(get_v1_world_caravan_path_Query),
    'get_v1_world_caravans': TypeAdapter(get_v1_world_caravans_Query),
    'get_v1_world_grid': TypeAdapter(get_v1_world_grid_Query),
    'get_v1_world_grid_area': TypeAdapter(get_v1_world_grid_area_Query),
    'get_v1_world_player_settlements': TypeAdapter(get_v1_world_player_settlements_Query),
    'get_v1_world_settlements': TypeAdapter(get_v1_world_settlements_Query),
    'get_v1_world_sites': TypeAdapter(get_v1_world_sites_Query),
    'get_v1_world_tile': TypeAdapter(get_v1_world_tile_Query),
    'get_v1_world_tile_coordinates': TypeAdapter(get_v1_world_tile_coordinates_Query),
    'get_v1_world_tile_details': TypeAdapter(get_v1_world_tile_details_Query),
    'get_v2_colonist_detailed': TypeAdapter(get_v2_colonist_detailed_Query),
    'get_v2_colonists_detailed': TypeAdapter(get_v2_colonists_detailed_Query),
    'get_api_v2_construction_contracts': TypeAdapter(get_api_v2_construction_contracts_Query),
    'construction_definitions': TypeAdapter(construction_definitions_Query),
    'construction_inspect': TypeAdapter(construction_inspect_Query),
    'get_api_v2_construction_openapi': TypeAdapter(get_api_v2_construction_openapi_Query),
    'construction_place': TypeAdapter(construction_place_Query),
    'construction_rooms': TypeAdapter(construction_rooms_Query),
    'construction_state': TypeAdapter(construction_state_Query),
    'construction_area': TypeAdapter(construction_area_Query),
    'get_v1_work_settings': TypeAdapter(get_v1_work_settings_Query),
    'post_v1_work_settings': TypeAdapter(post_v1_work_settings_Query),
    'get_v1_map_resource_overview': TypeAdapter(get_v1_map_resource_overview_Query),
    'get_v1_map_construction_work': TypeAdapter(get_v1_map_construction_work_Query),
    'orders_unforbid_all': TypeAdapter(orders_unforbid_all_Query),
    'orders_forbidden_overview': TypeAdapter(orders_forbidden_overview_Query),
}

BODY_TYPES = {
    'post_v1_builder_blueprint': TypeAdapter(Input_PasteAreaRequestDto),
    'post_v1_builder_check_zone': TypeAdapter(Input_CheckZoneRequestDto),
    'post_v1_builder_copy': TypeAdapter(Input_CopyAreaRequestDto),
    'post_v1_builder_paste': TypeAdapter(Input_PasteAreaRequestDto),
    'put_v1_buildings_bill_reorder': TypeAdapter(Input_BillReorderRequest),
    'put_v1_buildings_bill_suspend': TypeAdapter(Input_BillSuspendRequest),
    'put_v1_buildings_bill_update': TypeAdapter(Input_UpdateBillRequest),
    'post_v1_buildings_bills_add': TypeAdapter(Input_CreateBillRequest),
    'post_v1_camera_screenshot': TypeAdapter(Input_Camera_CameraScreenshotRequestDto),
    'post_v1_camera_screenshot_native': TypeAdapter(Input_Camera_NativeScreenshotRequestDto),
    'post_v1_camera_stream_setup': TypeAdapter(Input_StreamConfigDto),
    'post_v1_client_learning_mark_learned': TypeAdapter(Input_LearningConceptMarkDto),
    'post_v1_colonist_time_assignment': TypeAdapter(Input_PawnTimeAssignmentRequestDto),
    'post_v1_colonist_work_priority': TypeAdapter(Input_WorkPriorityRequestDto),
    'post_v1_colonists_work_priority': TypeAdapter(Input_ColonistsWorkPrioritiesRequestDto),
    'get_v1_def_all': TypeAdapter(Input_AllDefsRequestDto),
    'post_v1_dev_console': TypeAdapter(Input_DebugConsoleRequest),
    'post_v1_dev_stuff_color': TypeAdapter(Input_StuffColorRequest),
    'post_v1_faction_change_goodwill': TypeAdapter(Input_FactionChangeRelationRequestDto),
    'post_v1_faction_goodwill': TypeAdapter(Input_FactionChangeRelationRequestDto),
    'post_v1_game_load': TypeAdapter(Input_GameLoadRequestDto),
    'post_v1_game_save': TypeAdapter(Input_GameSaveRequestDto),
    'post_v1_game_select_area': TypeAdapter(Input_SelectAreaRequestDto),
    'post_v1_game_send_letter': TypeAdapter(Input_SendLetterRequestDto),
    'post_v1_game_start': TypeAdapter(Input_NewGameStartRequestDto),
    'get_v1_incident_chance': TypeAdapter(Input_IncidentChanceRequestDto),
    'post_v1_incident_trigger': TypeAdapter(Input_TriggerIncidentRequestDto),
    'post_v1_item_change_image': TypeAdapter(Input_ImageUploadRequest),
    'post_v1_item_spawn': TypeAdapter(Input_SpawnItemRequestDto),
    'post_v1_lords_create': TypeAdapter(Input_LordCreateRequestDto),
    'post_v1_map_destroy_rect': TypeAdapter(Input_DestroyRectRequestDto),
    'post_v1_map_droppod': TypeAdapter(Input_SpawnDropPodRequestDto),
    'post_v1_map_repair_positions': TypeAdapter(Input_RepairPositionsRequestDto),
    'post_v1_map_repair_rect': TypeAdapter(Input_RepairRectRequestDto),
    'get_v1_map_things_at': TypeAdapter(Input_ThingsAtCellRequestDto),
    'post_v1_map_zone_growing': TypeAdapter(Input_CreateGrowingZoneRequestDto),
    'post_v1_map_zone_stockpile': TypeAdapter(Input_CreateStockpileRequestDto),
    'post_v1_map_zone_stockpile_update': TypeAdapter(Input_UpdateStockpileRequestDto),
    'post_v1_mods_configure': TypeAdapter(Input_ConfigureModsRequestDto),
    'post_v1_order_designate_area': TypeAdapter(Input_DesignateRequestDto),
    'post_v1_pawn_edit_apparel': TypeAdapter(Input_PawnApparelRequest),
    'post_v1_pawn_edit_basic': TypeAdapter(Input_PawnBasicRequest),
    'post_v1_pawn_edit_faction': TypeAdapter(Input_PawnFactionRequest),
    'post_v1_pawn_edit_health': TypeAdapter(Input_PawnHealthRequest),
    'post_v1_pawn_edit_inventory': TypeAdapter(Input_PawnInventoryRequest),
    'post_v1_pawn_edit_needs': TypeAdapter(Input_PawnNeedsRequest),
    'post_v1_pawn_edit_position': TypeAdapter(Input_PawnPositionRequest),
    'post_v1_pawn_edit_skills': TypeAdapter(Input_PawnSkillsRequest),
    'post_v1_pawn_edit_status': TypeAdapter(Input_PawnStatusRequest),
    'post_v1_pawn_edit_traits': TypeAdapter(Input_PawnTraitsRequest),
    'post_v1_pawn_job': TypeAdapter(Input_PawnJobRequestDto),
    'post_v1_pawn_medical_bed_rest': TypeAdapter(Input_MedicalBedRestRequestDto),
    'post_v1_pawn_medical_tend': TypeAdapter(Input_MedicalTendRequestDto),
    'post_v1_pawn_spawn': TypeAdapter(Input_PawnSpawnRequestDto),
    'post_v1_pawns_interactions_force': TypeAdapter(Input_ForceInteractionRequestDto),
    'post_v1_pawns_relations_add': TypeAdapter(Input_AddRelationRequestDto),
    'post_v1_things_set_forbidden': TypeAdapter(Input_SetForbiddenRequestDto),
    'post_v1_ui_announce': TypeAdapter(Input_OverlayRequestDto),
    'post_v1_ui_dialog': TypeAdapter(Input_WindowDialogRequestDto),
    'post_v1_ui_message': TypeAdapter(Input_WindowMessageRequestDto),
    'post_v1_ui_window_close': TypeAdapter(Input_WindowCloseRequestDto),
    'construction_definitions': TypeAdapter(Construction_DefinitionQuery),
    'construction_inspect': TypeAdapter(Construction_ConstructionRequest),
    'construction_place': TypeAdapter(Construction_ConstructionRequest),
    'construction_rooms': TypeAdapter(Construction_RoomQuery),
    'construction_state': TypeAdapter(Construction_MapQuery),
    'construction_area': TypeAdapter(Construction_AreaQuery),
    'post_v1_work_settings': TypeAdapter(WorkSettings),
    'orders_unforbid_all': TypeAdapter(Construction_AllowAllRequest),
    'orders_forbidden_overview': TypeAdapter(Construction_AllowAllRequest),
}

RESPONSE_TYPES = {
    'get_api_openapi_json': TypeAdapter(dict[str, JsonValue]),
    'post_v1_builder_blueprint': TypeAdapter(ApiResult),
    'post_v1_builder_check_zone': TypeAdapter(ApiResult_CheckZoneResultDto),
    'post_v1_builder_copy': TypeAdapter(ApiResult_BlueprintDto),
    'post_v1_builder_paste': TypeAdapter(ApiResult),
    'get_v1_buildings_bill': TypeAdapter(ApiResult_BillDto),
    'delete_v1_buildings_bill_remove': TypeAdapter(ApiResult),
    'put_v1_buildings_bill_reorder': TypeAdapter(ApiResult),
    'put_v1_buildings_bill_suspend': TypeAdapter(ApiResult),
    'put_v1_buildings_bill_update': TypeAdapter(ApiResult_BillDto),
    'get_v1_buildings_bills': TypeAdapter(ApiResult_List_BillDto),
    'post_v1_buildings_bills_add': TypeAdapter(ApiResult_BillDto),
    'delete_v1_buildings_bills_remove': TypeAdapter(ApiResult),
    'get_v1_buildings_recipes': TypeAdapter(ApiResult_List_RecipeDto),
    'post_v1_cache_clear': TypeAdapter(ApiResult_ServerCacheResponseDto),
    'post_v1_cache_disable': TypeAdapter(ApiResult_ServerCacheResponseDto),
    'post_v1_cache_enable': TypeAdapter(ApiResult_ServerCacheResponseDto),
    'get_v1_cache_status': TypeAdapter(ApiResult_Anonymouse219319f76),
    'post_v1_camera_change_position': TypeAdapter(ApiResult),
    'post_v1_camera_change_zoom': TypeAdapter(ApiResult),
    'post_v1_camera_follow_pawn': TypeAdapter(ApiResult),
    'post_v1_camera_screenshot': TypeAdapter(ApiResult_Camera_CameraScreenshotResponseDto),
    'post_v1_camera_screenshot_native': TypeAdapter(ApiResult_string),
    'post_v1_camera_stream_setup': TypeAdapter(ApiResult),
    'post_v1_camera_stream_start': TypeAdapter(ApiResult),
    'get_v1_camera_stream_status': TypeAdapter(ApiResult_StreamStatusDto),
    'post_v1_camera_stream_stop': TypeAdapter(ApiResult),
    'get_v1_client_learning_active': TypeAdapter(ApiResult_List_LearningConceptDto),
    'get_v1_client_learning_all': TypeAdapter(ApiResult_List_LearningConceptDto),
    'get_v1_client_learning_concept': TypeAdapter(ApiResult_LearningConceptDto),
    'get_v1_client_learning_defs': TypeAdapter(ApiResult_List_string),
    'post_v1_client_learning_mark_learned': TypeAdapter(ApiResult_bool),
    'get_v1_colonist': TypeAdapter(ApiResult_PawnDto),
    'get_v1_colonist_body_image': TypeAdapter(ApiResult_BodyPartsDto),
    'get_v1_colonist_detailed': TypeAdapter(ApiResult_ApiV1PawnDetailedDto),
    'get_v1_colonist_inventory': TypeAdapter(ApiResult_PawnInventoryDto),
    'get_v1_colonist_opinion_about': TypeAdapter(ApiResult_OpinionAboutPawnDto),
    'post_v1_colonist_time_assignment': TypeAdapter(ApiResult),
    'post_v1_colonist_work_priority': TypeAdapter(ApiResult),
    'get_v1_colonists': TypeAdapter(ApiResult_List_PawnDto),
    'get_v1_colonists_detailed': TypeAdapter(ApiResult_List_ApiV1PawnDetailedDto),
    'get_v1_colonists_positions': TypeAdapter(ApiResult_List_PawnPositionDto),
    'post_v1_colonists_work_priority': TypeAdapter(ApiResult),
    'get_v1_core_docs_export': TypeAdapter(Core_ApiDocumentation),
    'get_v1_datetime': TypeAdapter(ApiResult_MapTimeDto),
    'get_v1_datetime_tile': TypeAdapter(ApiResult_MapTimeDto),
    'get_v1_def_all': TypeAdapter(ApiResult_DefsDto),
    'post_v1_deselect': TypeAdapter(ApiResult),
    'post_v1_dev_console': TypeAdapter(ApiResult),
    'get_v1_dev_endpoints': TypeAdapter(ApiResult_EndpointListDto),
    'get_v1_dev_materials_atlas': TypeAdapter(ApiResult_MaterialsAtlasList),
    'post_v1_dev_materials_atlas_clear': TypeAdapter(ApiResult),
    'post_v1_dev_stuff_color': TypeAdapter(ApiResult),
    'get_v1_docs': TypeAdapter(ApiResult_Core_ApiDocumentation),
    'get_v1_docs_health': TypeAdapter(ApiResult_Anonymousb7f0d20d59),
    'get_v1_faction': TypeAdapter(ApiResult_FactionDto),
    'post_v1_faction_change_goodwill': TypeAdapter(ApiResult_FactionChangeRelationResponceDto),
    'get_v1_faction_def': TypeAdapter(ApiResult_FactionDefDto),
    'post_v1_faction_goodwill': TypeAdapter(ApiResult_FactionChangeRelationResponceDto),
    'get_v1_faction_icon': TypeAdapter(ApiResult_FactionIconImageDto),
    'get_v1_faction_player': TypeAdapter(ApiResult_FactionDto),
    'get_v1_faction_relation_with': TypeAdapter(ApiResult_FactionRelationDto),
    'get_v1_faction_relations': TypeAdapter(ApiResult_FactionRelationsDto),
    'get_v1_factions': TypeAdapter(ApiResult_List_FactionsDto),
    'get_v1_game_defs_interactions': TypeAdapter(ApiResult_List_InteractionDefDto),
    'post_v1_game_load': TypeAdapter(ApiResult),
    'post_v1_game_main_menu': TypeAdapter(ApiResult),
    'post_v1_game_quit': TypeAdapter(ApiResult),
    'post_v1_game_save': TypeAdapter(ApiResult),
    'post_v1_game_select_area': TypeAdapter(ApiResult),
    'post_v1_game_send_letter': TypeAdapter(ApiResult),
    'get_v1_game_settings': TypeAdapter(ApiResult_GameSettingsDto),
    'get_v1_game_settings_run_in_background': TypeAdapter(ApiResult_bool),
    'post_v1_game_settings_toggle_run_in_background': TypeAdapter(ApiResult_bool),
    'post_v1_game_speed': TypeAdapter(ApiResult),
    'post_v1_game_start': TypeAdapter(ApiResult),
    'post_v1_game_start_devquick': TypeAdapter(ApiResult),
    'get_v1_game_state': TypeAdapter(ApiResult_GameStateDto),
    'get_v1_incident_chance': TypeAdapter(ApiResult_IncidentChanceDto),
    'post_v1_incident_trigger': TypeAdapter(ApiResult),
    'get_v1_incidents': TypeAdapter(ApiResult_IncidentsDto),
    'get_v1_incidents_top': TypeAdapter(ApiResult_List_IncidentWeightDto),
    'post_v1_item_change_image': TypeAdapter(ApiResult),
    'get_v1_item_image': TypeAdapter(ApiResult_ImageDto),
    'get_v1_item_recipes': TypeAdapter(ApiResult_ItemRecipesDto),
    'get_v1_item_sources': TypeAdapter(ApiResult_ThingSourcesDto),
    'post_v1_item_spawn': TypeAdapter(ApiResult),
    'post_v1_jobs_make_equip': TypeAdapter(ApiResult),
    'get_v1_lords': TypeAdapter(ApiResult_List_LordDto),
    'post_v1_lords_create': TypeAdapter(Union[ApiResult, ApiResult_LordCreateDto]),
    'get_v1_map_animals': TypeAdapter(ApiResult_List_AnimalDto),
    'get_v1_map_building_info': TypeAdapter(ApiResult_BuildingDto),
    'post_v1_map_building_power': TypeAdapter(ApiResult),
    'get_v1_map_buildings': TypeAdapter(ApiResult_List_BuildingDto),
    'get_v1_map_creatures_summary': TypeAdapter(ApiResult_MapCreaturesSummaryDto),
    'post_v1_map_destroy_corpses': TypeAdapter(ApiResult),
    'post_v1_map_destroy_forbidden': TypeAdapter(ApiResult),
    'post_v1_map_destroy_rect': TypeAdapter(ApiResult),
    'post_v1_map_droppod': TypeAdapter(ApiResult),
    'get_v1_map_farm_summary': TypeAdapter(ApiResult_MapFarmSummaryDto),
    'get_v1_map_fog_grid': TypeAdapter(ApiResult_FogGridDto),
    'get_v1_map_ore': TypeAdapter(ApiResult_Map_OreDataDto),
    'get_v1_map_pawns': TypeAdapter(ApiResult_List_PawnDto),
    'get_v1_map_plants': TypeAdapter(ApiResult_List_ThingDto),
    'get_v1_map_power_info': TypeAdapter(ApiResult_MapPowerInfoDto),
    'post_v1_map_repair_positions': TypeAdapter(ApiResult),
    'post_v1_map_repair_rect': TypeAdapter(ApiResult),
    'get_v1_map_rooms': TypeAdapter(ApiResult_MapRoomsDto),
    'get_v1_map_terrain': TypeAdapter(ApiResult_MapTerrainDto),
    'get_v1_map_things': TypeAdapter(ApiResult_List_ThingDto),
    'get_v1_map_things_at': TypeAdapter(ApiResult_List_ThingDto),
    'get_v1_map_things_radius': TypeAdapter(ApiResult_List_ThingDto),
    'get_v1_map_weather': TypeAdapter(ApiResult_MapWeatherDto),
    'post_v1_map_weather_change': TypeAdapter(ApiResult),
    'get_v1_map_work_tables': TypeAdapter(ApiResult_List_WorkTableDto),
    'get_v1_map_zone_growing': TypeAdapter(ApiResult_GrowingZoneDto),
    'post_v1_map_zone_growing': TypeAdapter(ApiResult_GrowingZoneDto),
    'post_v1_map_zone_stockpile': TypeAdapter(ApiResult_StockpileResponseDto),
    'delete_v1_map_zone_stockpile_delete': TypeAdapter(ApiResult),
    'post_v1_map_zone_stockpile_update': TypeAdapter(ApiResult_StockpileResponseDto),
    'get_v1_map_zones': TypeAdapter(ApiResult_MapZonesDto),
    'get_v1_maps': TypeAdapter(ApiResult_List_MapDto),
    'post_v1_mods_configure': TypeAdapter(ApiResult),
    'get_v1_mods_info': TypeAdapter(ApiResult_ModInfoDto),
    'get_v1_mods_list': TypeAdapter(ApiResult_List_ModInfoDto),
    'get_v1_mods_preview': TypeAdapter(ApiResult_string),
    'post_v1_open_tab': TypeAdapter(ApiResult),
    'post_v1_order_designate_area': TypeAdapter(ApiResult),
    'get_v1_outfits': TypeAdapter(ApiResult_List_OutfitDto),
    'post_v1_pawn_edit_apparel': TypeAdapter(ApiResult),
    'post_v1_pawn_edit_basic': TypeAdapter(ApiResult),
    'post_v1_pawn_edit_faction': TypeAdapter(ApiResult),
    'post_v1_pawn_edit_health': TypeAdapter(ApiResult),
    'post_v1_pawn_edit_inventory': TypeAdapter(ApiResult),
    'post_v1_pawn_edit_needs': TypeAdapter(ApiResult),
    'post_v1_pawn_edit_position': TypeAdapter(ApiResult),
    'post_v1_pawn_edit_skills': TypeAdapter(ApiResult),
    'post_v1_pawn_edit_status': TypeAdapter(ApiResult),
    'post_v1_pawn_edit_traits': TypeAdapter(ApiResult),
    'post_v1_pawn_job': TypeAdapter(ApiResult),
    'post_v1_pawn_medical_bed_rest': TypeAdapter(ApiResult),
    'post_v1_pawn_medical_tend': TypeAdapter(ApiResult),
    'get_v1_pawn_portrait_image': TypeAdapter(ApiResult_ImageDto),
    'post_v1_pawn_spawn': TypeAdapter(ApiResult_PawnSpawnDto),
    'get_v1_pawns_details': TypeAdapter(ApiResult_PawnDetailedDto),
    'get_v1_pawns_interactions': TypeAdapter(ApiResult_PawnInteractionStatusDto),
    'post_v1_pawns_interactions_force': TypeAdapter(ApiResult),
    'get_v1_pawns_interactions_log': TypeAdapter(ApiResult_PawnInteractionLogDto),
    'get_v1_pawns_inventory': TypeAdapter(ApiResult_PawnInventoryDto),
    'get_v1_pawns_opinions': TypeAdapter(ApiResult_List_PawnOpinionDto),
    'get_v1_pawns_relations': TypeAdapter(ApiResult_PawnRelationsDto),
    'post_v1_pawns_relations_add': TypeAdapter(ApiResult),
    'delete_v1_pawns_relations_remove': TypeAdapter(ApiResult),
    'get_v1_quests': TypeAdapter(ApiResult_QuestsDto),
    'get_v1_research_finished': TypeAdapter(ApiResult_ResearchFinishedDto),
    'get_v1_research_progress': TypeAdapter(ApiResult_ResearchProjectDto),
    'get_v1_research_project': TypeAdapter(ApiResult_ResearchProjectDto),
    'post_v1_research_stop': TypeAdapter(ApiResult),
    'get_v1_research_summary': TypeAdapter(ApiResult_ResearchSummaryDto),
    'post_v1_research_target': TypeAdapter(ApiResult_ResearchProjectDto),
    'get_v1_research_tree': TypeAdapter(ApiResult_ResearchTreeDto),
    'get_v1_resources_storages_summary': TypeAdapter(ApiResult_StoragesSummaryDto),
    'get_v1_resources_stored': TypeAdapter(Union[ApiResult_Dictionary_string__List_ThingDto, ApiResult_List_ThingDto]),
    'get_v1_resources_summary': TypeAdapter(ApiResult_ResourcesSummaryDto),
    'post_v1_select': TypeAdapter(ApiResult),
    'get_v1_terrain_image': TypeAdapter(ApiResult_ImageDto),
    'post_v1_things_set_forbidden': TypeAdapter(ApiResult),
    'get_v1_time_assignments': TypeAdapter(ApiResult_List_TimeAssignmentDto),
    'get_v1_traders_defs': TypeAdapter(ApiResult_List_TraderKindDto),
    'get_v1_trait_def': TypeAdapter(ApiResult_TraitDefDto),
    'get_v1_ui_alerts': TypeAdapter(ApiResult_List_UI_AlertDto),
    'post_v1_ui_announce': TypeAdapter(ApiResult),
    'post_v1_ui_dialog': TypeAdapter(ApiResult),
    'post_v1_ui_message': TypeAdapter(ApiResult),
    'post_v1_ui_window_close': TypeAdapter(ApiResult_WindowCloseResultDto),
    'get_v1_ui_windows': TypeAdapter(ApiResult_List_OpenWindowDto),
    'get_v1_version': TypeAdapter(ApiResult_VersionDto),
    'get_v1_work_list': TypeAdapter(ApiResult_WorkListDto),
    'get_v1_world_caravan_path': TypeAdapter(ApiResult_CaravanPathDto),
    'get_v1_world_caravans': TypeAdapter(ApiResult_List_CaravanDto),
    'get_v1_world_grid': TypeAdapter(ApiResult_List_TileDto),
    'get_v1_world_grid_area': TypeAdapter(ApiResult_List_TileDto),
    'get_v1_world_player_settlements': TypeAdapter(ApiResult_List_SettlementDto),
    'get_v1_world_settlements': TypeAdapter(ApiResult_List_SettlementDto),
    'get_v1_world_sites': TypeAdapter(ApiResult_List_SiteDto),
    'get_v1_world_tile': TypeAdapter(ApiResult_TileDto),
    'get_v1_world_tile_coordinates': TypeAdapter(ApiResult_CoordinatesDto),
    'get_v1_world_tile_details': TypeAdapter(ApiResult_TileDetailsDto),
    'get_v2_colonist_detailed': TypeAdapter(ApiResult_PawnDetailedRequestDto),
    'get_v2_colonists_detailed': TypeAdapter(ApiResult_List_PawnDetailedRequestDto),
    'get_api_v2_construction_contracts': TypeAdapter(dict[str, JsonValue]),
    'construction_definitions': TypeAdapter(Construction_PageBuildingDefinition),
    'construction_inspect': TypeAdapter(Construction_ConstructionResult),
    'get_api_v2_construction_openapi': TypeAdapter(dict[str, JsonValue]),
    'construction_place': TypeAdapter(Construction_ConstructionResult),
    'construction_rooms': TypeAdapter(Construction_PageRoomDto),
    'construction_state': TypeAdapter(Construction_ConstructionState),
    'construction_area': TypeAdapter(Construction_AreaResult),
    'get_v1_work_settings': TypeAdapter(WorkSettings),
    'post_v1_work_settings': TypeAdapter(WorkSettings),
    'get_v1_map_resource_overview': TypeAdapter(MapResourceOverview),
    'get_v1_map_construction_work': TypeAdapter(ConstructionWorkOverview),
    'orders_unforbid_all': TypeAdapter(Construction_AllowAllResult),
    'orders_forbidden_overview': TypeAdapter(Construction_AllowAllResult),
}

class HttpOperations:
    async def _call(self, operation_id, *, query=None, body=None):
        raise NotImplementedError

    async def get_api_openapi_json(self, *, query: get_api_openapi_json_Query | None = None) -> dict[str, JsonValue]:
        return await self._call('get_api_openapi_json', query=query)

    async def post_v1_builder_blueprint(self, *, query: post_v1_builder_blueprint_Query | None = None, body: Input_PasteAreaRequestDto | None = None) -> ApiResult:
        return await self._call('post_v1_builder_blueprint', query=query, body=body)

    async def post_v1_builder_check_zone(self, *, query: post_v1_builder_check_zone_Query | None = None, body: Input_CheckZoneRequestDto | None = None) -> ApiResult_CheckZoneResultDto:
        return await self._call('post_v1_builder_check_zone', query=query, body=body)

    async def post_v1_builder_copy(self, *, query: post_v1_builder_copy_Query | None = None, body: Input_CopyAreaRequestDto | None = None) -> ApiResult_BlueprintDto:
        return await self._call('post_v1_builder_copy', query=query, body=body)

    async def post_v1_builder_paste(self, *, query: post_v1_builder_paste_Query | None = None, body: Input_PasteAreaRequestDto | None = None) -> ApiResult:
        return await self._call('post_v1_builder_paste', query=query, body=body)

    async def get_v1_buildings_bill(self, *, query: get_v1_buildings_bill_Query | None = None) -> ApiResult_BillDto:
        return await self._call('get_v1_buildings_bill', query=query)

    async def delete_v1_buildings_bill_remove(self, *, query: delete_v1_buildings_bill_remove_Query | None = None) -> ApiResult:
        return await self._call('delete_v1_buildings_bill_remove', query=query)

    async def put_v1_buildings_bill_reorder(self, *, query: put_v1_buildings_bill_reorder_Query | None = None, body: Input_BillReorderRequest | None = None) -> ApiResult:
        return await self._call('put_v1_buildings_bill_reorder', query=query, body=body)

    async def put_v1_buildings_bill_suspend(self, *, query: put_v1_buildings_bill_suspend_Query | None = None, body: Input_BillSuspendRequest | None = None) -> ApiResult:
        return await self._call('put_v1_buildings_bill_suspend', query=query, body=body)

    async def put_v1_buildings_bill_update(self, *, query: put_v1_buildings_bill_update_Query | None = None, body: Input_UpdateBillRequest | None = None) -> ApiResult_BillDto:
        return await self._call('put_v1_buildings_bill_update', query=query, body=body)

    async def get_v1_buildings_bills(self, *, query: get_v1_buildings_bills_Query | None = None) -> ApiResult_List_BillDto:
        return await self._call('get_v1_buildings_bills', query=query)

    async def post_v1_buildings_bills_add(self, *, query: post_v1_buildings_bills_add_Query | None = None, body: Input_CreateBillRequest | None = None) -> ApiResult_BillDto:
        return await self._call('post_v1_buildings_bills_add', query=query, body=body)

    async def delete_v1_buildings_bills_remove(self, *, query: delete_v1_buildings_bills_remove_Query | None = None) -> ApiResult:
        return await self._call('delete_v1_buildings_bills_remove', query=query)

    async def get_v1_buildings_recipes(self, *, query: get_v1_buildings_recipes_Query | None = None) -> ApiResult_List_RecipeDto:
        return await self._call('get_v1_buildings_recipes', query=query)

    async def post_v1_cache_clear(self, *, query: post_v1_cache_clear_Query | None = None) -> ApiResult_ServerCacheResponseDto:
        return await self._call('post_v1_cache_clear', query=query)

    async def post_v1_cache_disable(self, *, query: post_v1_cache_disable_Query | None = None) -> ApiResult_ServerCacheResponseDto:
        return await self._call('post_v1_cache_disable', query=query)

    async def post_v1_cache_enable(self, *, query: post_v1_cache_enable_Query | None = None) -> ApiResult_ServerCacheResponseDto:
        return await self._call('post_v1_cache_enable', query=query)

    async def get_v1_cache_status(self, *, query: get_v1_cache_status_Query | None = None) -> ApiResult_Anonymouse219319f76:
        return await self._call('get_v1_cache_status', query=query)

    async def post_v1_camera_change_position(self, *, query: post_v1_camera_change_position_Query | None = None) -> ApiResult:
        return await self._call('post_v1_camera_change_position', query=query)

    async def post_v1_camera_change_zoom(self, *, query: post_v1_camera_change_zoom_Query | None = None) -> ApiResult:
        return await self._call('post_v1_camera_change_zoom', query=query)

    async def post_v1_camera_follow_pawn(self, *, query: post_v1_camera_follow_pawn_Query | None = None) -> ApiResult:
        return await self._call('post_v1_camera_follow_pawn', query=query)

    async def post_v1_camera_screenshot(self, *, query: post_v1_camera_screenshot_Query | None = None, body: Input_Camera_CameraScreenshotRequestDto | None = None) -> ApiResult_Camera_CameraScreenshotResponseDto:
        return await self._call('post_v1_camera_screenshot', query=query, body=body)

    async def post_v1_camera_screenshot_native(self, *, query: post_v1_camera_screenshot_native_Query | None = None, body: Input_Camera_NativeScreenshotRequestDto | None = None) -> ApiResult_string:
        return await self._call('post_v1_camera_screenshot_native', query=query, body=body)

    async def post_v1_camera_stream_setup(self, *, query: post_v1_camera_stream_setup_Query | None = None, body: Input_StreamConfigDto | None = None) -> ApiResult:
        return await self._call('post_v1_camera_stream_setup', query=query, body=body)

    async def post_v1_camera_stream_start(self, *, query: post_v1_camera_stream_start_Query | None = None) -> ApiResult:
        return await self._call('post_v1_camera_stream_start', query=query)

    async def get_v1_camera_stream_status(self, *, query: get_v1_camera_stream_status_Query | None = None) -> ApiResult_StreamStatusDto:
        return await self._call('get_v1_camera_stream_status', query=query)

    async def post_v1_camera_stream_stop(self, *, query: post_v1_camera_stream_stop_Query | None = None) -> ApiResult:
        return await self._call('post_v1_camera_stream_stop', query=query)

    async def get_v1_client_learning_active(self, *, query: get_v1_client_learning_active_Query | None = None) -> ApiResult_List_LearningConceptDto:
        return await self._call('get_v1_client_learning_active', query=query)

    async def get_v1_client_learning_all(self, *, query: get_v1_client_learning_all_Query | None = None) -> ApiResult_List_LearningConceptDto:
        return await self._call('get_v1_client_learning_all', query=query)

    async def get_v1_client_learning_concept(self, *, query: get_v1_client_learning_concept_Query | None = None) -> ApiResult_LearningConceptDto:
        return await self._call('get_v1_client_learning_concept', query=query)

    async def get_v1_client_learning_defs(self, *, query: get_v1_client_learning_defs_Query | None = None) -> ApiResult_List_string:
        return await self._call('get_v1_client_learning_defs', query=query)

    async def post_v1_client_learning_mark_learned(self, *, query: post_v1_client_learning_mark_learned_Query | None = None, body: Input_LearningConceptMarkDto | None = None) -> ApiResult_bool:
        return await self._call('post_v1_client_learning_mark_learned', query=query, body=body)

    async def get_v1_colonist(self, *, query: get_v1_colonist_Query | None = None) -> ApiResult_PawnDto:
        return await self._call('get_v1_colonist', query=query)

    async def get_v1_colonist_body_image(self, *, query: get_v1_colonist_body_image_Query | None = None) -> ApiResult_BodyPartsDto:
        return await self._call('get_v1_colonist_body_image', query=query)

    async def get_v1_colonist_detailed(self, *, query: get_v1_colonist_detailed_Query | None = None) -> ApiResult_ApiV1PawnDetailedDto:
        return await self._call('get_v1_colonist_detailed', query=query)

    async def get_v1_colonist_inventory(self, *, query: get_v1_colonist_inventory_Query | None = None) -> ApiResult_PawnInventoryDto:
        return await self._call('get_v1_colonist_inventory', query=query)

    async def get_v1_colonist_opinion_about(self, *, query: get_v1_colonist_opinion_about_Query | None = None) -> ApiResult_OpinionAboutPawnDto:
        return await self._call('get_v1_colonist_opinion_about', query=query)

    async def post_v1_colonist_time_assignment(self, *, query: post_v1_colonist_time_assignment_Query | None = None, body: Input_PawnTimeAssignmentRequestDto | None = None) -> ApiResult:
        return await self._call('post_v1_colonist_time_assignment', query=query, body=body)

    async def post_v1_colonist_work_priority(self, *, query: post_v1_colonist_work_priority_Query | None = None, body: Input_WorkPriorityRequestDto | None = None) -> ApiResult:
        return await self._call('post_v1_colonist_work_priority', query=query, body=body)

    async def get_v1_colonists(self, *, query: get_v1_colonists_Query | None = None) -> ApiResult_List_PawnDto:
        return await self._call('get_v1_colonists', query=query)

    async def get_v1_colonists_detailed(self, *, query: get_v1_colonists_detailed_Query | None = None) -> ApiResult_List_ApiV1PawnDetailedDto:
        return await self._call('get_v1_colonists_detailed', query=query)

    async def get_v1_colonists_positions(self, *, query: get_v1_colonists_positions_Query | None = None) -> ApiResult_List_PawnPositionDto:
        return await self._call('get_v1_colonists_positions', query=query)

    async def post_v1_colonists_work_priority(self, *, query: post_v1_colonists_work_priority_Query | None = None, body: Input_ColonistsWorkPrioritiesRequestDto | None = None) -> ApiResult:
        return await self._call('post_v1_colonists_work_priority', query=query, body=body)

    async def get_v1_core_docs_export(self, *, query: get_v1_core_docs_export_Query | None = None) -> Core_ApiDocumentation:
        return await self._call('get_v1_core_docs_export', query=query)

    async def get_v1_datetime(self, *, query: get_v1_datetime_Query | None = None) -> ApiResult_MapTimeDto:
        return await self._call('get_v1_datetime', query=query)

    async def get_v1_datetime_tile(self, *, query: get_v1_datetime_tile_Query | None = None) -> ApiResult_MapTimeDto:
        return await self._call('get_v1_datetime_tile', query=query)

    async def get_v1_def_all(self, *, query: get_v1_def_all_Query | None = None, body: Input_AllDefsRequestDto | None = None) -> ApiResult_DefsDto:
        return await self._call('get_v1_def_all', query=query, body=body)

    async def post_v1_deselect(self, *, query: post_v1_deselect_Query | None = None) -> ApiResult:
        return await self._call('post_v1_deselect', query=query)

    async def post_v1_dev_console(self, *, query: post_v1_dev_console_Query | None = None, body: Input_DebugConsoleRequest | None = None) -> ApiResult:
        return await self._call('post_v1_dev_console', query=query, body=body)

    async def get_v1_dev_endpoints(self, *, query: get_v1_dev_endpoints_Query | None = None) -> ApiResult_EndpointListDto:
        return await self._call('get_v1_dev_endpoints', query=query)

    async def get_v1_dev_materials_atlas(self, *, query: get_v1_dev_materials_atlas_Query | None = None) -> ApiResult_MaterialsAtlasList:
        return await self._call('get_v1_dev_materials_atlas', query=query)

    async def post_v1_dev_materials_atlas_clear(self, *, query: post_v1_dev_materials_atlas_clear_Query | None = None) -> ApiResult:
        return await self._call('post_v1_dev_materials_atlas_clear', query=query)

    async def post_v1_dev_stuff_color(self, *, query: post_v1_dev_stuff_color_Query | None = None, body: Input_StuffColorRequest | None = None) -> ApiResult:
        return await self._call('post_v1_dev_stuff_color', query=query, body=body)

    async def get_v1_docs(self, *, query: get_v1_docs_Query | None = None) -> ApiResult_Core_ApiDocumentation:
        return await self._call('get_v1_docs', query=query)

    async def get_v1_docs_health(self, *, query: get_v1_docs_health_Query | None = None) -> ApiResult_Anonymousb7f0d20d59:
        return await self._call('get_v1_docs_health', query=query)

    async def get_v1_faction(self, *, query: get_v1_faction_Query | None = None) -> ApiResult_FactionDto:
        return await self._call('get_v1_faction', query=query)

    async def post_v1_faction_change_goodwill(self, *, query: post_v1_faction_change_goodwill_Query | None = None, body: Input_FactionChangeRelationRequestDto | None = None) -> ApiResult_FactionChangeRelationResponceDto:
        return await self._call('post_v1_faction_change_goodwill', query=query, body=body)

    async def get_v1_faction_def(self, *, query: get_v1_faction_def_Query | None = None) -> ApiResult_FactionDefDto:
        return await self._call('get_v1_faction_def', query=query)

    async def post_v1_faction_goodwill(self, *, query: post_v1_faction_goodwill_Query | None = None, body: Input_FactionChangeRelationRequestDto | None = None) -> ApiResult_FactionChangeRelationResponceDto:
        return await self._call('post_v1_faction_goodwill', query=query, body=body)

    async def get_v1_faction_icon(self, *, query: get_v1_faction_icon_Query | None = None) -> ApiResult_FactionIconImageDto:
        return await self._call('get_v1_faction_icon', query=query)

    async def get_v1_faction_player(self, *, query: get_v1_faction_player_Query | None = None) -> ApiResult_FactionDto:
        return await self._call('get_v1_faction_player', query=query)

    async def get_v1_faction_relation_with(self, *, query: get_v1_faction_relation_with_Query | None = None) -> ApiResult_FactionRelationDto:
        return await self._call('get_v1_faction_relation_with', query=query)

    async def get_v1_faction_relations(self, *, query: get_v1_faction_relations_Query | None = None) -> ApiResult_FactionRelationsDto:
        return await self._call('get_v1_faction_relations', query=query)

    async def get_v1_factions(self, *, query: get_v1_factions_Query | None = None) -> ApiResult_List_FactionsDto:
        return await self._call('get_v1_factions', query=query)

    async def get_v1_game_defs_interactions(self, *, query: get_v1_game_defs_interactions_Query | None = None) -> ApiResult_List_InteractionDefDto:
        return await self._call('get_v1_game_defs_interactions', query=query)

    async def post_v1_game_load(self, *, query: post_v1_game_load_Query | None = None, body: Input_GameLoadRequestDto | None = None) -> ApiResult:
        return await self._call('post_v1_game_load', query=query, body=body)

    async def post_v1_game_main_menu(self, *, query: post_v1_game_main_menu_Query | None = None) -> ApiResult:
        return await self._call('post_v1_game_main_menu', query=query)

    async def post_v1_game_quit(self, *, query: post_v1_game_quit_Query | None = None) -> ApiResult:
        return await self._call('post_v1_game_quit', query=query)

    async def post_v1_game_save(self, *, query: post_v1_game_save_Query | None = None, body: Input_GameSaveRequestDto | None = None) -> ApiResult:
        return await self._call('post_v1_game_save', query=query, body=body)

    async def post_v1_game_select_area(self, *, query: post_v1_game_select_area_Query | None = None, body: Input_SelectAreaRequestDto | None = None) -> ApiResult:
        return await self._call('post_v1_game_select_area', query=query, body=body)

    async def post_v1_game_send_letter(self, *, query: post_v1_game_send_letter_Query | None = None, body: Input_SendLetterRequestDto | None = None) -> ApiResult:
        return await self._call('post_v1_game_send_letter', query=query, body=body)

    async def get_v1_game_settings(self, *, query: get_v1_game_settings_Query | None = None) -> ApiResult_GameSettingsDto:
        return await self._call('get_v1_game_settings', query=query)

    async def get_v1_game_settings_run_in_background(self, *, query: get_v1_game_settings_run_in_background_Query | None = None) -> ApiResult_bool:
        return await self._call('get_v1_game_settings_run_in_background', query=query)

    async def post_v1_game_settings_toggle_run_in_background(self, *, query: post_v1_game_settings_toggle_run_in_background_Query | None = None) -> ApiResult_bool:
        return await self._call('post_v1_game_settings_toggle_run_in_background', query=query)

    async def post_v1_game_speed(self, *, query: post_v1_game_speed_Query | None = None) -> ApiResult:
        return await self._call('post_v1_game_speed', query=query)

    async def post_v1_game_start(self, *, query: post_v1_game_start_Query | None = None, body: Input_NewGameStartRequestDto | None = None) -> ApiResult:
        return await self._call('post_v1_game_start', query=query, body=body)

    async def post_v1_game_start_devquick(self, *, query: post_v1_game_start_devquick_Query | None = None) -> ApiResult:
        return await self._call('post_v1_game_start_devquick', query=query)

    async def get_v1_game_state(self, *, query: get_v1_game_state_Query | None = None) -> ApiResult_GameStateDto:
        return await self._call('get_v1_game_state', query=query)

    async def get_v1_incident_chance(self, *, query: get_v1_incident_chance_Query | None = None, body: Input_IncidentChanceRequestDto | None = None) -> ApiResult_IncidentChanceDto:
        return await self._call('get_v1_incident_chance', query=query, body=body)

    async def post_v1_incident_trigger(self, *, query: post_v1_incident_trigger_Query | None = None, body: Input_TriggerIncidentRequestDto | None = None) -> ApiResult:
        return await self._call('post_v1_incident_trigger', query=query, body=body)

    async def get_v1_incidents(self, *, query: get_v1_incidents_Query | None = None) -> ApiResult_IncidentsDto:
        return await self._call('get_v1_incidents', query=query)

    async def get_v1_incidents_top(self, *, query: get_v1_incidents_top_Query | None = None) -> ApiResult_List_IncidentWeightDto:
        return await self._call('get_v1_incidents_top', query=query)

    async def post_v1_item_change_image(self, *, query: post_v1_item_change_image_Query | None = None, body: Input_ImageUploadRequest | None = None) -> ApiResult:
        return await self._call('post_v1_item_change_image', query=query, body=body)

    async def get_v1_item_image(self, *, query: get_v1_item_image_Query | None = None) -> ApiResult_ImageDto:
        return await self._call('get_v1_item_image', query=query)

    async def get_v1_item_recipes(self, *, query: get_v1_item_recipes_Query | None = None) -> ApiResult_ItemRecipesDto:
        return await self._call('get_v1_item_recipes', query=query)

    async def get_v1_item_sources(self, *, query: get_v1_item_sources_Query | None = None) -> ApiResult_ThingSourcesDto:
        return await self._call('get_v1_item_sources', query=query)

    async def post_v1_item_spawn(self, *, query: post_v1_item_spawn_Query | None = None, body: Input_SpawnItemRequestDto | None = None) -> ApiResult:
        return await self._call('post_v1_item_spawn', query=query, body=body)

    async def post_v1_jobs_make_equip(self, *, query: post_v1_jobs_make_equip_Query | None = None) -> ApiResult:
        return await self._call('post_v1_jobs_make_equip', query=query)

    async def get_v1_lords(self, *, query: get_v1_lords_Query | None = None) -> ApiResult_List_LordDto:
        return await self._call('get_v1_lords', query=query)

    async def post_v1_lords_create(self, *, query: post_v1_lords_create_Query | None = None, body: Input_LordCreateRequestDto | None = None) -> Union[ApiResult, ApiResult_LordCreateDto]:
        return await self._call('post_v1_lords_create', query=query, body=body)

    async def get_v1_map_animals(self, *, query: get_v1_map_animals_Query | None = None) -> ApiResult_List_AnimalDto:
        return await self._call('get_v1_map_animals', query=query)

    async def get_v1_map_building_info(self, *, query: get_v1_map_building_info_Query | None = None) -> ApiResult_BuildingDto:
        return await self._call('get_v1_map_building_info', query=query)

    async def post_v1_map_building_power(self, *, query: post_v1_map_building_power_Query | None = None) -> ApiResult:
        return await self._call('post_v1_map_building_power', query=query)

    async def get_v1_map_buildings(self, *, query: get_v1_map_buildings_Query | None = None) -> ApiResult_List_BuildingDto:
        return await self._call('get_v1_map_buildings', query=query)

    async def get_v1_map_creatures_summary(self, *, query: get_v1_map_creatures_summary_Query | None = None) -> ApiResult_MapCreaturesSummaryDto:
        return await self._call('get_v1_map_creatures_summary', query=query)

    async def post_v1_map_destroy_corpses(self, *, query: post_v1_map_destroy_corpses_Query | None = None) -> ApiResult:
        return await self._call('post_v1_map_destroy_corpses', query=query)

    async def post_v1_map_destroy_forbidden(self, *, query: post_v1_map_destroy_forbidden_Query | None = None) -> ApiResult:
        return await self._call('post_v1_map_destroy_forbidden', query=query)

    async def post_v1_map_destroy_rect(self, *, query: post_v1_map_destroy_rect_Query | None = None, body: Input_DestroyRectRequestDto | None = None) -> ApiResult:
        return await self._call('post_v1_map_destroy_rect', query=query, body=body)

    async def post_v1_map_droppod(self, *, query: post_v1_map_droppod_Query | None = None, body: Input_SpawnDropPodRequestDto | None = None) -> ApiResult:
        return await self._call('post_v1_map_droppod', query=query, body=body)

    async def get_v1_map_farm_summary(self, *, query: get_v1_map_farm_summary_Query | None = None) -> ApiResult_MapFarmSummaryDto:
        return await self._call('get_v1_map_farm_summary', query=query)

    async def get_v1_map_fog_grid(self, *, query: get_v1_map_fog_grid_Query | None = None) -> ApiResult_FogGridDto:
        return await self._call('get_v1_map_fog_grid', query=query)

    async def get_v1_map_ore(self, *, query: get_v1_map_ore_Query | None = None) -> ApiResult_Map_OreDataDto:
        return await self._call('get_v1_map_ore', query=query)

    async def get_v1_map_pawns(self, *, query: get_v1_map_pawns_Query | None = None) -> ApiResult_List_PawnDto:
        return await self._call('get_v1_map_pawns', query=query)

    async def get_v1_map_plants(self, *, query: get_v1_map_plants_Query | None = None) -> ApiResult_List_ThingDto:
        return await self._call('get_v1_map_plants', query=query)

    async def get_v1_map_power_info(self, *, query: get_v1_map_power_info_Query | None = None) -> ApiResult_MapPowerInfoDto:
        return await self._call('get_v1_map_power_info', query=query)

    async def post_v1_map_repair_positions(self, *, query: post_v1_map_repair_positions_Query | None = None, body: Input_RepairPositionsRequestDto | None = None) -> ApiResult:
        return await self._call('post_v1_map_repair_positions', query=query, body=body)

    async def post_v1_map_repair_rect(self, *, query: post_v1_map_repair_rect_Query | None = None, body: Input_RepairRectRequestDto | None = None) -> ApiResult:
        return await self._call('post_v1_map_repair_rect', query=query, body=body)

    async def get_v1_map_rooms(self, *, query: get_v1_map_rooms_Query | None = None) -> ApiResult_MapRoomsDto:
        return await self._call('get_v1_map_rooms', query=query)

    async def get_v1_map_terrain(self, *, query: get_v1_map_terrain_Query | None = None) -> ApiResult_MapTerrainDto:
        return await self._call('get_v1_map_terrain', query=query)

    async def get_v1_map_things(self, *, query: get_v1_map_things_Query | None = None) -> ApiResult_List_ThingDto:
        return await self._call('get_v1_map_things', query=query)

    async def get_v1_map_things_at(self, *, query: get_v1_map_things_at_Query | None = None, body: Input_ThingsAtCellRequestDto | None = None) -> ApiResult_List_ThingDto:
        return await self._call('get_v1_map_things_at', query=query, body=body)

    async def get_v1_map_things_radius(self, *, query: get_v1_map_things_radius_Query | None = None) -> ApiResult_List_ThingDto:
        return await self._call('get_v1_map_things_radius', query=query)

    async def get_v1_map_weather(self, *, query: get_v1_map_weather_Query | None = None) -> ApiResult_MapWeatherDto:
        return await self._call('get_v1_map_weather', query=query)

    async def post_v1_map_weather_change(self, *, query: post_v1_map_weather_change_Query | None = None) -> ApiResult:
        return await self._call('post_v1_map_weather_change', query=query)

    async def get_v1_map_work_tables(self, *, query: get_v1_map_work_tables_Query | None = None) -> ApiResult_List_WorkTableDto:
        return await self._call('get_v1_map_work_tables', query=query)

    async def get_v1_map_zone_growing(self, *, query: get_v1_map_zone_growing_Query | None = None) -> ApiResult_GrowingZoneDto:
        return await self._call('get_v1_map_zone_growing', query=query)

    async def post_v1_map_zone_growing(self, *, query: post_v1_map_zone_growing_Query | None = None, body: Input_CreateGrowingZoneRequestDto | None = None) -> ApiResult_GrowingZoneDto:
        return await self._call('post_v1_map_zone_growing', query=query, body=body)

    async def post_v1_map_zone_stockpile(self, *, query: post_v1_map_zone_stockpile_Query | None = None, body: Input_CreateStockpileRequestDto | None = None) -> ApiResult_StockpileResponseDto:
        return await self._call('post_v1_map_zone_stockpile', query=query, body=body)

    async def delete_v1_map_zone_stockpile_delete(self, *, query: delete_v1_map_zone_stockpile_delete_Query | None = None) -> ApiResult:
        return await self._call('delete_v1_map_zone_stockpile_delete', query=query)

    async def post_v1_map_zone_stockpile_update(self, *, query: post_v1_map_zone_stockpile_update_Query | None = None, body: Input_UpdateStockpileRequestDto | None = None) -> ApiResult_StockpileResponseDto:
        return await self._call('post_v1_map_zone_stockpile_update', query=query, body=body)

    async def get_v1_map_zones(self, *, query: get_v1_map_zones_Query | None = None) -> ApiResult_MapZonesDto:
        return await self._call('get_v1_map_zones', query=query)

    async def get_v1_maps(self, *, query: get_v1_maps_Query | None = None) -> ApiResult_List_MapDto:
        return await self._call('get_v1_maps', query=query)

    async def post_v1_mods_configure(self, *, query: post_v1_mods_configure_Query | None = None, body: Input_ConfigureModsRequestDto | None = None) -> ApiResult:
        return await self._call('post_v1_mods_configure', query=query, body=body)

    async def get_v1_mods_info(self, *, query: get_v1_mods_info_Query | None = None) -> ApiResult_ModInfoDto:
        return await self._call('get_v1_mods_info', query=query)

    async def get_v1_mods_list(self, *, query: get_v1_mods_list_Query | None = None) -> ApiResult_List_ModInfoDto:
        return await self._call('get_v1_mods_list', query=query)

    async def get_v1_mods_preview(self, *, query: get_v1_mods_preview_Query | None = None) -> ApiResult_string:
        return await self._call('get_v1_mods_preview', query=query)

    async def post_v1_open_tab(self, *, query: post_v1_open_tab_Query | None = None) -> ApiResult:
        return await self._call('post_v1_open_tab', query=query)

    async def post_v1_order_designate_area(self, *, query: post_v1_order_designate_area_Query | None = None, body: Input_DesignateRequestDto | None = None) -> ApiResult:
        return await self._call('post_v1_order_designate_area', query=query, body=body)

    async def get_v1_outfits(self, *, query: get_v1_outfits_Query | None = None) -> ApiResult_List_OutfitDto:
        return await self._call('get_v1_outfits', query=query)

    async def post_v1_pawn_edit_apparel(self, *, query: post_v1_pawn_edit_apparel_Query | None = None, body: Input_PawnApparelRequest | None = None) -> ApiResult:
        return await self._call('post_v1_pawn_edit_apparel', query=query, body=body)

    async def post_v1_pawn_edit_basic(self, *, query: post_v1_pawn_edit_basic_Query | None = None, body: Input_PawnBasicRequest | None = None) -> ApiResult:
        return await self._call('post_v1_pawn_edit_basic', query=query, body=body)

    async def post_v1_pawn_edit_faction(self, *, query: post_v1_pawn_edit_faction_Query | None = None, body: Input_PawnFactionRequest | None = None) -> ApiResult:
        return await self._call('post_v1_pawn_edit_faction', query=query, body=body)

    async def post_v1_pawn_edit_health(self, *, query: post_v1_pawn_edit_health_Query | None = None, body: Input_PawnHealthRequest | None = None) -> ApiResult:
        return await self._call('post_v1_pawn_edit_health', query=query, body=body)

    async def post_v1_pawn_edit_inventory(self, *, query: post_v1_pawn_edit_inventory_Query | None = None, body: Input_PawnInventoryRequest | None = None) -> ApiResult:
        return await self._call('post_v1_pawn_edit_inventory', query=query, body=body)

    async def post_v1_pawn_edit_needs(self, *, query: post_v1_pawn_edit_needs_Query | None = None, body: Input_PawnNeedsRequest | None = None) -> ApiResult:
        return await self._call('post_v1_pawn_edit_needs', query=query, body=body)

    async def post_v1_pawn_edit_position(self, *, query: post_v1_pawn_edit_position_Query | None = None, body: Input_PawnPositionRequest | None = None) -> ApiResult:
        return await self._call('post_v1_pawn_edit_position', query=query, body=body)

    async def post_v1_pawn_edit_skills(self, *, query: post_v1_pawn_edit_skills_Query | None = None, body: Input_PawnSkillsRequest | None = None) -> ApiResult:
        return await self._call('post_v1_pawn_edit_skills', query=query, body=body)

    async def post_v1_pawn_edit_status(self, *, query: post_v1_pawn_edit_status_Query | None = None, body: Input_PawnStatusRequest | None = None) -> ApiResult:
        return await self._call('post_v1_pawn_edit_status', query=query, body=body)

    async def post_v1_pawn_edit_traits(self, *, query: post_v1_pawn_edit_traits_Query | None = None, body: Input_PawnTraitsRequest | None = None) -> ApiResult:
        return await self._call('post_v1_pawn_edit_traits', query=query, body=body)

    async def post_v1_pawn_job(self, *, query: post_v1_pawn_job_Query | None = None, body: Input_PawnJobRequestDto | None = None) -> ApiResult:
        return await self._call('post_v1_pawn_job', query=query, body=body)

    async def post_v1_pawn_medical_bed_rest(self, *, query: post_v1_pawn_medical_bed_rest_Query | None = None, body: Input_MedicalBedRestRequestDto | None = None) -> ApiResult:
        return await self._call('post_v1_pawn_medical_bed_rest', query=query, body=body)

    async def post_v1_pawn_medical_tend(self, *, query: post_v1_pawn_medical_tend_Query | None = None, body: Input_MedicalTendRequestDto | None = None) -> ApiResult:
        return await self._call('post_v1_pawn_medical_tend', query=query, body=body)

    async def get_v1_pawn_portrait_image(self, *, query: get_v1_pawn_portrait_image_Query | None = None) -> ApiResult_ImageDto:
        return await self._call('get_v1_pawn_portrait_image', query=query)

    async def post_v1_pawn_spawn(self, *, query: post_v1_pawn_spawn_Query | None = None, body: Input_PawnSpawnRequestDto | None = None) -> ApiResult_PawnSpawnDto:
        return await self._call('post_v1_pawn_spawn', query=query, body=body)

    async def get_v1_pawns_details(self, *, query: get_v1_pawns_details_Query | None = None) -> ApiResult_PawnDetailedDto:
        return await self._call('get_v1_pawns_details', query=query)

    async def get_v1_pawns_interactions(self, *, query: get_v1_pawns_interactions_Query | None = None) -> ApiResult_PawnInteractionStatusDto:
        return await self._call('get_v1_pawns_interactions', query=query)

    async def post_v1_pawns_interactions_force(self, *, query: post_v1_pawns_interactions_force_Query | None = None, body: Input_ForceInteractionRequestDto | None = None) -> ApiResult:
        return await self._call('post_v1_pawns_interactions_force', query=query, body=body)

    async def get_v1_pawns_interactions_log(self, *, query: get_v1_pawns_interactions_log_Query | None = None) -> ApiResult_PawnInteractionLogDto:
        return await self._call('get_v1_pawns_interactions_log', query=query)

    async def get_v1_pawns_inventory(self, *, query: get_v1_pawns_inventory_Query | None = None) -> ApiResult_PawnInventoryDto:
        return await self._call('get_v1_pawns_inventory', query=query)

    async def get_v1_pawns_opinions(self, *, query: get_v1_pawns_opinions_Query | None = None) -> ApiResult_List_PawnOpinionDto:
        return await self._call('get_v1_pawns_opinions', query=query)

    async def get_v1_pawns_relations(self, *, query: get_v1_pawns_relations_Query | None = None) -> ApiResult_PawnRelationsDto:
        return await self._call('get_v1_pawns_relations', query=query)

    async def post_v1_pawns_relations_add(self, *, query: post_v1_pawns_relations_add_Query | None = None, body: Input_AddRelationRequestDto | None = None) -> ApiResult:
        return await self._call('post_v1_pawns_relations_add', query=query, body=body)

    async def delete_v1_pawns_relations_remove(self, *, query: delete_v1_pawns_relations_remove_Query | None = None) -> ApiResult:
        return await self._call('delete_v1_pawns_relations_remove', query=query)

    async def get_v1_quests(self, *, query: get_v1_quests_Query | None = None) -> ApiResult_QuestsDto:
        return await self._call('get_v1_quests', query=query)

    async def get_v1_research_finished(self, *, query: get_v1_research_finished_Query | None = None) -> ApiResult_ResearchFinishedDto:
        return await self._call('get_v1_research_finished', query=query)

    async def get_v1_research_progress(self, *, query: get_v1_research_progress_Query | None = None) -> ApiResult_ResearchProjectDto:
        return await self._call('get_v1_research_progress', query=query)

    async def get_v1_research_project(self, *, query: get_v1_research_project_Query | None = None) -> ApiResult_ResearchProjectDto:
        return await self._call('get_v1_research_project', query=query)

    async def post_v1_research_stop(self, *, query: post_v1_research_stop_Query | None = None) -> ApiResult:
        return await self._call('post_v1_research_stop', query=query)

    async def get_v1_research_summary(self, *, query: get_v1_research_summary_Query | None = None) -> ApiResult_ResearchSummaryDto:
        return await self._call('get_v1_research_summary', query=query)

    async def post_v1_research_target(self, *, query: post_v1_research_target_Query | None = None) -> ApiResult_ResearchProjectDto:
        return await self._call('post_v1_research_target', query=query)

    async def get_v1_research_tree(self, *, query: get_v1_research_tree_Query | None = None) -> ApiResult_ResearchTreeDto:
        return await self._call('get_v1_research_tree', query=query)

    async def get_v1_resources_storages_summary(self, *, query: get_v1_resources_storages_summary_Query | None = None) -> ApiResult_StoragesSummaryDto:
        return await self._call('get_v1_resources_storages_summary', query=query)

    async def get_v1_resources_stored(self, *, query: get_v1_resources_stored_Query | None = None) -> Union[ApiResult_Dictionary_string__List_ThingDto, ApiResult_List_ThingDto]:
        return await self._call('get_v1_resources_stored', query=query)

    async def get_v1_resources_summary(self, *, query: get_v1_resources_summary_Query | None = None) -> ApiResult_ResourcesSummaryDto:
        return await self._call('get_v1_resources_summary', query=query)

    async def post_v1_select(self, *, query: post_v1_select_Query | None = None) -> ApiResult:
        return await self._call('post_v1_select', query=query)

    async def get_v1_terrain_image(self, *, query: get_v1_terrain_image_Query | None = None) -> ApiResult_ImageDto:
        return await self._call('get_v1_terrain_image', query=query)

    async def post_v1_things_set_forbidden(self, *, query: post_v1_things_set_forbidden_Query | None = None, body: Input_SetForbiddenRequestDto | None = None) -> ApiResult:
        return await self._call('post_v1_things_set_forbidden', query=query, body=body)

    async def get_v1_time_assignments(self, *, query: get_v1_time_assignments_Query | None = None) -> ApiResult_List_TimeAssignmentDto:
        return await self._call('get_v1_time_assignments', query=query)

    async def get_v1_traders_defs(self, *, query: get_v1_traders_defs_Query | None = None) -> ApiResult_List_TraderKindDto:
        return await self._call('get_v1_traders_defs', query=query)

    async def get_v1_trait_def(self, *, query: get_v1_trait_def_Query | None = None) -> ApiResult_TraitDefDto:
        return await self._call('get_v1_trait_def', query=query)

    async def get_v1_ui_alerts(self, *, query: get_v1_ui_alerts_Query | None = None) -> ApiResult_List_UI_AlertDto:
        return await self._call('get_v1_ui_alerts', query=query)

    async def post_v1_ui_announce(self, *, query: post_v1_ui_announce_Query | None = None, body: Input_OverlayRequestDto | None = None) -> ApiResult:
        return await self._call('post_v1_ui_announce', query=query, body=body)

    async def post_v1_ui_dialog(self, *, query: post_v1_ui_dialog_Query | None = None, body: Input_WindowDialogRequestDto | None = None) -> ApiResult:
        return await self._call('post_v1_ui_dialog', query=query, body=body)

    async def post_v1_ui_message(self, *, query: post_v1_ui_message_Query | None = None, body: Input_WindowMessageRequestDto | None = None) -> ApiResult:
        return await self._call('post_v1_ui_message', query=query, body=body)

    async def post_v1_ui_window_close(self, *, query: post_v1_ui_window_close_Query | None = None, body: Input_WindowCloseRequestDto | None = None) -> ApiResult_WindowCloseResultDto:
        return await self._call('post_v1_ui_window_close', query=query, body=body)

    async def get_v1_ui_windows(self, *, query: get_v1_ui_windows_Query | None = None) -> ApiResult_List_OpenWindowDto:
        return await self._call('get_v1_ui_windows', query=query)

    async def get_v1_version(self, *, query: get_v1_version_Query | None = None) -> ApiResult_VersionDto:
        return await self._call('get_v1_version', query=query)

    async def get_v1_work_list(self, *, query: get_v1_work_list_Query | None = None) -> ApiResult_WorkListDto:
        return await self._call('get_v1_work_list', query=query)

    async def get_v1_world_caravan_path(self, *, query: get_v1_world_caravan_path_Query | None = None) -> ApiResult_CaravanPathDto:
        return await self._call('get_v1_world_caravan_path', query=query)

    async def get_v1_world_caravans(self, *, query: get_v1_world_caravans_Query | None = None) -> ApiResult_List_CaravanDto:
        return await self._call('get_v1_world_caravans', query=query)

    async def get_v1_world_grid(self, *, query: get_v1_world_grid_Query | None = None) -> ApiResult_List_TileDto:
        return await self._call('get_v1_world_grid', query=query)

    async def get_v1_world_grid_area(self, *, query: get_v1_world_grid_area_Query | None = None) -> ApiResult_List_TileDto:
        return await self._call('get_v1_world_grid_area', query=query)

    async def get_v1_world_player_settlements(self, *, query: get_v1_world_player_settlements_Query | None = None) -> ApiResult_List_SettlementDto:
        return await self._call('get_v1_world_player_settlements', query=query)

    async def get_v1_world_settlements(self, *, query: get_v1_world_settlements_Query | None = None) -> ApiResult_List_SettlementDto:
        return await self._call('get_v1_world_settlements', query=query)

    async def get_v1_world_sites(self, *, query: get_v1_world_sites_Query | None = None) -> ApiResult_List_SiteDto:
        return await self._call('get_v1_world_sites', query=query)

    async def get_v1_world_tile(self, *, query: get_v1_world_tile_Query | None = None) -> ApiResult_TileDto:
        return await self._call('get_v1_world_tile', query=query)

    async def get_v1_world_tile_coordinates(self, *, query: get_v1_world_tile_coordinates_Query | None = None) -> ApiResult_CoordinatesDto:
        return await self._call('get_v1_world_tile_coordinates', query=query)

    async def get_v1_world_tile_details(self, *, query: get_v1_world_tile_details_Query | None = None) -> ApiResult_TileDetailsDto:
        return await self._call('get_v1_world_tile_details', query=query)

    async def get_v2_colonist_detailed(self, *, query: get_v2_colonist_detailed_Query | None = None) -> ApiResult_PawnDetailedRequestDto:
        return await self._call('get_v2_colonist_detailed', query=query)

    async def get_v2_colonists_detailed(self, *, query: get_v2_colonists_detailed_Query | None = None) -> ApiResult_List_PawnDetailedRequestDto:
        return await self._call('get_v2_colonists_detailed', query=query)

    async def get_api_v2_construction_contracts(self, *, query: get_api_v2_construction_contracts_Query | None = None) -> dict[str, JsonValue]:
        return await self._call('get_api_v2_construction_contracts', query=query)

    async def construction_definitions(self, *, query: construction_definitions_Query | None = None, body: Construction_DefinitionQuery | None = None) -> Construction_PageBuildingDefinition:
        return await self._call('construction_definitions', query=query, body=body)

    async def construction_inspect(self, *, query: construction_inspect_Query | None = None, body: Construction_ConstructionRequest | None = None) -> Construction_ConstructionResult:
        return await self._call('construction_inspect', query=query, body=body)

    async def get_api_v2_construction_openapi(self, *, query: get_api_v2_construction_openapi_Query | None = None) -> dict[str, JsonValue]:
        return await self._call('get_api_v2_construction_openapi', query=query)

    async def construction_place(self, *, query: construction_place_Query | None = None, body: Construction_ConstructionRequest | None = None) -> Construction_ConstructionResult:
        return await self._call('construction_place', query=query, body=body)

    async def construction_rooms(self, *, query: construction_rooms_Query | None = None, body: Construction_RoomQuery | None = None) -> Construction_PageRoomDto:
        return await self._call('construction_rooms', query=query, body=body)

    async def construction_state(self, *, query: construction_state_Query | None = None, body: Construction_MapQuery | None = None) -> Construction_ConstructionState:
        return await self._call('construction_state', query=query, body=body)

    async def construction_area(self, *, query: construction_area_Query | None = None, body: Construction_AreaQuery | None = None) -> Construction_AreaResult:
        return await self._call('construction_area', query=query, body=body)

    async def get_v1_work_settings(self, *, query: get_v1_work_settings_Query | None = None) -> WorkSettings:
        return await self._call('get_v1_work_settings', query=query)

    async def post_v1_work_settings(self, *, query: post_v1_work_settings_Query | None = None, body: WorkSettings | None = None) -> WorkSettings:
        return await self._call('post_v1_work_settings', query=query, body=body)

    async def get_v1_map_resource_overview(self, *, query: get_v1_map_resource_overview_Query | None = None) -> MapResourceOverview:
        return await self._call('get_v1_map_resource_overview', query=query)

    async def get_v1_map_construction_work(self, *, query: get_v1_map_construction_work_Query | None = None) -> ConstructionWorkOverview:
        return await self._call('get_v1_map_construction_work', query=query)

    async def orders_unforbid_all(self, *, query: orders_unforbid_all_Query | None = None, body: Construction_AllowAllRequest | None = None) -> Construction_AllowAllResult:
        return await self._call('orders_unforbid_all', query=query, body=body)

    async def orders_forbidden_overview(self, *, query: orders_forbidden_overview_Query | None = None, body: Construction_AllowAllRequest | None = None) -> Construction_AllowAllResult:
        return await self._call('orders_forbidden_overview', query=query, body=body)
