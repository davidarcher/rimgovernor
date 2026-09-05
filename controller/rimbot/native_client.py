"""Typed native transport boundary. Domain objects enter and leave this client."""
import httpx
from pydantic import ValidationError
from .native_models import REQUEST_TYPES, RESPONSE_TYPES, NativeObject, ContractError, ConstructionRequest, ConstructionResult

class NativeIntegrationError(RuntimeError):
    pass

class NativeClient:
    def __init__(self, http, lock, catalog, invalidate):
        self.http,self.lock,self.catalog,self.invalidate=http,lock,catalog,invalidate

    async def call(self, name: str, request: NativeObject | dict) -> NativeObject:
        contract=self.catalog.get(name)
        request_type=REQUEST_TYPES[name]
        payload=request if isinstance(request,request_type) else request_type.model_validate(request)
        async with self.lock:
            if contract['write']:self.invalidate()
            try:
                response=await self.http.post(contract['path'],json=payload.model_dump())
                if response.is_error:
                    error=ContractError.model_validate(response.json())
                    raise NativeIntegrationError(f'{name}: {error.code}: {error.message}')
                result=RESPONSE_TYPES[name].model_validate(response.json())
                if isinstance(result,ConstructionResult):
                    if [item.placement for item in result.items] != payload.buildings:
                        raise NativeIntegrationError(f'{name}: response placements do not match the request.')
                    if result.accepted != all(item.state!='rejected' for item in result.items):
                        raise NativeIntegrationError(f'{name}: inconsistent acceptance state.')
                    if any(item.state in ('blueprint','frame','built') and item.thing_id is None for item in result.items):
                        raise NativeIntegrationError(f'{name}: observed construction is missing its native ID.')
                    if name=='construction_place' and result.accepted and any(item.state=='ready' for item in result.items):
                        raise NativeIntegrationError(f'{name}: accepted order has no observed placement.')
                return result
            except (ValidationError,ValueError) as e:
                raise NativeIntegrationError(f'{name}: native response violated its published contract: {e}') from e
            except httpx.HTTPError as e:
                raise NativeIntegrationError(f'{name}: transport failed; outcome unknown, no automatic retry: {e}') from e

    async def inspect(self, request: ConstructionRequest) -> ConstructionResult:
        return await self.call('construction_inspect',request)

    async def place(self, request: ConstructionRequest) -> ConstructionResult:
        return await self.call('construction_place',request)
