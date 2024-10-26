<script lang='ts'>
    import { onMount } from 'svelte';
    import type BasicProps from './basicProps';

    interface Props extends BasicProps {
        onnext: () => void;
    }

    let {
        canAdvance = $bindable(),
        onnext,
    }: Props = $props();

    onMount(() => {
        canAdvance = true;
    });
</script>

<div class='root'>
    <h1>Heliosphere Installer</h1>

    <div class='logos'>
        <figure>
            <img
                src='/favicon.svg'
                alt='Heliosphere logo'
            />

            <figcaption>Heliosphere</figcaption>
        </figure>

        <span>+</span>

        <figure>
            <img
                src='/penumbra.png'
                alt='Penumbra logo'
            />

            <figcaption>Penumbra</figcaption>
        </figure>
    </div>

    <button
        class='fullwidth'
        onclick={onnext}
    >
        Get started
    </button>
</div>


<style lang='scss'>
    .root {
        display: flex;
        flex-direction: column;
        justify-content: space-between;
        align-items: center;
        gap: var(--pico-spacing);
        text-align: center;
        height: 100%;
    }

    * {
        margin: 0;
    }

    .logos {
        flex-grow: 1;
        width: 100%;

        display: grid;
        grid-template-columns: 1fr auto 1fr;
        gap: calc(var(--pico-spacing) * 2);
        // align-content: center;
        align-items: center;

        img {
            min-height: 0;
            min-width: 0;
            width: auto;
        }

        span {
            font-size: 5em;
            font-weight: bold;
        }
    }
</style>
