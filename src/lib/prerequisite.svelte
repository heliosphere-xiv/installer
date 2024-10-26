<script lang='ts'>
    import Icon from './icon.svelte';

    interface Props {
        label: string;
        state: boolean | undefined;
    }

    let {
        label,
        state,
    }: Props = $props();

    function getLabel(state: boolean | undefined): string {
        if (state == null) {
            return 'loading';
        }

        return state
            ? 'passed'
            : 'failed';
    }
</script>

<li>
    <div>
        <span
            aria-busy={state == null}
            aria-label={getLabel(state)}
        >
            {#if state != null}
                <Icon
                    inline
                    icon={state ? 'check-circle' : 'cross-circle'}
                    size='1rem'
                />
            {/if}
        </span>
        {label}
    </div>
</li>

<style lang='scss'>
    li {
        list-style: none;

        & > div {
            display: flex;
            align-items: center;
            gap: 1ch;
        }
    }
</style>
